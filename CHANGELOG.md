# HAF — Milestones & project history

A reverse-chronological-ish log of capabilities as they were first proven in-game, with the war stories
behind them. This is the project's memory: what was hard, how it was cracked, and when. For *what HAF does
today*, see the [README](README.md) and the [docs index](docs/README.md); this page is the trail that got us here.

Dates are first-verified-in-game. Many entries pre-date the dating convention and carry no date.

---

## Infrastructure

- **THE STRATEGIC FOOTPRINT DIED ON THE SECOND SAVE LOAD, AND NOTHING SAID SO (2026-08-23).** `footprintMaskInjected` is
  a `static bool` guarding a once-per-session injection. It was never reset — so on the second save load of a process
  `InjectReactorFootprint` returned immediately, while `ResetDistrictSessionState` had just queued the atlas and decal
  clone it guards for destruction. No strategic-zoom footprint at all, no error, no log line, until the game restarted.
  Re-injection is reachable (`loadedSelectorByKey` is `[SessionScoped]`, so the selector re-loads and the whole per-tile
  setup runs again); the latch was the only thing stopping it.
  **The leak beside it is why this is one fix and not two.** The injection creates three Unity objects and only one was
  owned — `ClonePrivateOutputLayer` tracks its clone, but the mask atlas and the decal clone went into
  `districtOwnedClones` never. That was survivable *only because the latch meant the method ran once per process*.
  Resetting the latch without tracking them would have converted a one-shot leak into a native atlas + decal leak on
  **every** save reload. Both now tracked. `reactorMaskTex` goes the other way and is marked `[ProcessLived]`: we decode
  it from a PNG once and every re-injection reuses it, so destroying it would leave the next session's atlas pointing at
  a dead texture.
  It is exactly the shape `SessionState`'s fence is documented as unable to police — a bare scalar, indistinguishable
  from a constant. Enumerating every one-way `static bool` in the plugin (set `= true`, never `= false`) found 35, of
  which **34 are legitimately process-lived** (`*Logged`, `*Dumped`, `*Resolved`) or are properties backed by
  `ScopedState`, which `S = new ScopedState()` already resets. So a blanket one-way-latch gate would have been 34 false
  positives and was deliberately not built; `Tests/DistrictSessionLatchTests.cs` is targeted instead — including a
  structural check that *every* Unity object the injection creates is owned, which catches the next clone added here
  rather than only the two that leaked.
  **The new test then found a second one.** A `[SessionScoped(Manual = …)]` field promises a hand-reset; asserting that
  something actually assigns or clears each one flagged `_subPawnScan`, whose sole reset site (the model session reset,
  commented *"session-1 sub-pawn components are corpses"*) set only the dirty flag. Never a correctness bug — the flag
  forces a rebuild before the next read — but the list went on holding references to every one of those destroyed Unity
  objects until something asked for the scan again. Now nulled there.
  Two of my own tests were caught cheating during the drill and are worth naming: one passed because **a comment
  quoting `_subPawnScan = null` satisfied its text search** (comments are stripped now — a prose mention is not a
  reset), and tightening an extraction regex silently stopped it matching any generic whose type argument list contains
  a space, dropping `_subPawnScan` out of the checked set entirely while the green line read the same. Canaries added
  for both awkward shapes. Same lesson as the catalog gate two entries down: **a guard that filters before it counts
  reports a smaller set, not a smaller number.**

- **A GATE THAT COUNTED 331 SITES AND SHOULD HAVE COUNTED 477 (2026-08-23).** Verifying the type-name migration in-game,
  the log showed `AccessTools.Field: Could not find field for type …Skeleton and name allMeshNames` **36 times in one
  session**. `typeprobe --exact` then said it plainly: **no assembly in the game declares `allMeshNames` at all** —
  `Skeleton` (base `MeshCollection`) carries only `skinnedMeshInfos`. So the probe missed every call, and the fallback
  branch it dropped into rebuilt the entire mesh-name array by reflection and then discarded it, because the
  `amnField?.SetValue(...)` meant to store it was equally null. Work done, thrown away, logged 36 times. The rename that
  actually lands is the `skinnedMeshInfos[0].MeshName` write above it. Dead block removed.
  **The real defect is that this is exactly what `check-catalog.sh` exists to catch, and it never looked.** The gate is
  regexes over source, and pass 1's accessor list stopped at HAF's own helpers while pass 2 gives up at the first `)` —
  so `AccessTools.Field(x.GetType(), "name")`, one of the commonest shapes in this codebase, was invisible. **146 sites
  across 70 distinct names**, never checked, while the pass line read "all 331 by-name literals catalogued". A filter
  that runs before the count cannot report its own blindness: an unseen shape is not flagged, it is silently subtracted
  from the denominator. Widened; the gate now sees 371. Drilled by putting a bogus member name in the newly-visible
  shape — **the gate as shipped at HEAD printed `OK — all 331 catalogued`, the widened one fails.**
  Of the four names the widening surfaced, three are real: `evolverDescriptorInstance` (`FxEvolverMaterial`) is a genuine
  feature path — the `[NonSerialized]` field `Instantiate` won't copy, which the reactor-footprint clone must carry by
  hand or `ResolveDependencies` NREs — so it is catalogued; `ContentTypeName` and `OutputEntries` sit in the
  `DistrictDebug`-gated dumps the catalog already exempts by policy, so they are site-allowlisted with that reason.
  The fourth was `allMeshNames`, which exists nowhere.
  **This is the third blind spot in this one guard** (08-22 the nested `GetMember(GetMember(…))` shape, 08-23 the
  `CachedField`/`GFA` family at 16 sites, now this at 146). Written up as its own lesson in [Testing](docs/Testing.md):
  when adding an accessor helper or call shape, extend `extract()` in the same commit, and drill by injecting a bad name
  *in the new shape* rather than re-running the gate. Found by a log line — not by the gate, not by `bindcheck`, not by
  review.

- **THE 7.85 MILLISECOND LOOKUP NOBODY WAS COUNTING (2026-08-23).** The question was "are all reflection lookups
  cached now?" — and the honest answer needed a benchmark, which then pointed somewhere nobody was looking.
  `AccessTools.Field`/`.Property`/`.Method` are 41–344 ns, and memoising them saves ~90 ns; `Type.GetField` is 20 ns
  and memoising it makes it **worse** (42 ns — the runtime already keeps a per-type member cache, so a `(Type, string)`
  tuple hash costs more than the lookup it replaces; see the GF entry below). But **`AccessTools.TypeByName` costs
  1,032 ns on a hit and 7.85 MILLISECONDS on a miss — every call, memoising nothing.** Three consecutive rounds:
  7.54 / 7.56 / 7.58 ms. That is a quarter of a 30 fps frame for *one failed type lookup*.
  **The danger is that it hides inside code written to be careful.** A failed lookup is what a renamed game type looks
  like, and the call sites all degrade gracefully — `if (type == null) return -1f;` — so the failure repeats forever
  instead of being reported. `DistrictInject.SchematicVis()` re-probed **two** types on every call and runs every 10
  frames all session: if either name broke, 15.7 ms every 10 frames, silently, presenting only as "the game feels
  bad". Worse still where the code looks *most* defensive — a `?? TypeByName(alt)` chain pays a **full** miss for
  every probe that is *meant* to fail, so the battle attack-replay's three-name probe cost ~15.7 ms of stall on the
  frame it first ran.
  Fixed by finishing a migration that was already half-done: the A6/A7 catalog sweeps had added
  `RenderFeatureProvider`, `AssetReferenceRepository`, `AnimationVariableNames` and friends as `GameBinding` accessors
  **but never migrated the call sites**, so the names lived in two places and the hot ones still paid full price. A8
  moved every one of them (19 ns on a hit, 13.7 µs to re-resolve a miss — 573x), turned each `??` chain into the
  accessor's own fallback list so the *winner* is what gets cached, and added a gate: `tools/check-catalog.sh` now
  fails on a raw `TypeByName` outside `GameBinding.cs`. Drilled — putting one back fails the gate.
  **Fallback ORDER turned out to be its own bug class, and `bindcheck` is blind to it** — it reports 132/132 whether an
  accessor resolved on its primary or limped in on a fallback. `typeprobe` against the shipped DLLs found two chains
  leading with a name that does not exist, i.e. paying a guaranteed 7.85 ms miss before reaching the real one:
  `GroundMaterialTextureData` led with a **nested** form that is NOT FOUND in this build, and the battle replay's
  `AnimationVariableNames` probed *three* names of which **none** match — the real type is
  `Amplitude.Mercury.AnimationVariableNames`, and the bare-name probe only ever worked via the simple-name `GetTypes()`
  walk over every loaded assembly. (My first pass got `GroundMaterialTextureData` backwards, preserving the call site's
  intent instead of checking the game; the typeprobe refuted it.) Both now lead with the name the build has, and the
  order is pinned by test.
  Also on the way: `GameBinding._typeCache` is a plain `Dictionary` with no thread contract stated; audited (the three sim-thread hooks
  reach reflection only via the `ConcurrentDictionary` in `UniversalInject`, never a `GameBinding` accessor) and
  annotated `[MainThread]` with the audit written beside it — a contract, not a bug fix.
  New `Tests/TypeResolutionTests.cs` pins the property the whole swap rests on: a **miss must not be memoised**, or
  every type HAF resolves before the game finishes loading would be dead for the session. It proves it the real way —
  ask for a type that doesn't exist, create it in a dynamic assembly, ask again. Drill note recorded in the file:
  `Cached` guards this twice (skip the null on write, ignore it on read) so *either* mutation alone survives and only
  both together are caught.

- **GF STAYS UNCACHED — AND THE 1,524 ns THAT JUSTIFIED CACHING IT WAS WRONG (2026-08-23).** The commit that reverted
  `DistrictInject.GF`'s cache quoted `AccessTools.Field` at 1,524 ns against a memoised 49 ns, "31x". A second harness
  says 131 ns. The first figure was cold-start metadata warm-up (~2,200 ns on the first-ever call for a type) leaking
  into too short an average. The revert itself was right and stands — `Type.GetField` 20 ns vs 42 ns cached — but the
  number used to justify its sibling `GFA` was inflated by 12x. Corrected here and in `docs/Performance.md` rather
  than quietly: `GFA` is still worth having (131 → 43 ns), just for a much smaller reason than claimed.

- **THE POSE HOOK'S FLOOR COST HALVED — TWO READS THE 08-21 PASS MISSED (2026-08-23).** With `SelectorTile` gone the
  top six was entirely pose work, and `PoseVanilla` stood out: **210 µs = 116 adds × 1,805 ns**, apparently 1.8 µs
  just to decide a pawn is *not* ours — 20× the 89 ns the district skip costs for the same kind of decision.
  **That framing was wrong, and reading the code corrected it:** `TryReadLastPawn` must read the manager's
  `pawnEntries` array and `pawnCount` before anything can tell whose pawn it is, so there is no cheaper decision to
  make — those two reads are the hook's floor. But they were still on plain reflection. The 08-21 pass compiled
  accessors for everything inside the PawnEntry **struct** (that is what took `PoseOurs` 25-57 µs → ~5 µs); the pawn
  **manager** is a different type and never got the same treatment. This page's own figure is ~0.5-1 µs per boxed
  reflection get on Mono, and two of those is the whole 1,805 ns. They run on EVERY add, ours and vanilla alike —
  162/frame in the measured scene. `PawnFast.EnsureMgrInit` compiles them per manager type with the reflection path
  kept as the fallback, as at every other PawnFast site. **Drilled in-game:** `pose vanilla` 1,805 → **1,006 ns/add**
  (−44%), `pose ours` 6,390 → **3,424 ns/add** (−46%), and the log confirms the mechanism —
  *"manager accessors compiled for PawnManager (pawnEntries, pawnCount)"*. `PoseOurs` falling too is the tell that
  this really was the shared floor rather than something vanilla-specific: both paths call `TryReadLastPawn` first.
  *(Totals are NOT comparable here — the verification scene had 46 vanilla adds against 116 and 17 ours against 46.
  ns-per-add is the scene-independent figure, which is exactly why the meter computes it.)* It also exposed a
  `FastMember` shape with **no coverage at all**: a reference-typed field read off a CLASS (`Castclass`, not
  `Unbox`) — everything before was boxed structs. Three tests added, drilled by emitting `Unbox` for the class
  path, which fails two tests and crashes the host on invalid IL.

- **`SelectorTile`: 219 µs → 6 µs — IT WAS WALKING 2,668 DISTRICTS TO FIND ONE (2026-08-23).** The measurement below
  came back unambiguous: `districts 2668 skipped 237.3 µs (89 ns ea), 1 ours 5.6 µs (5592 ns ea)`. The scan **was**
  the bucket. Three causes, all in the district tracking list: the poll iterated everything instead of the matches;
  **nothing ever removed a destroyed district**, so razed districts and every district from a previous in-session
  load stayed for the whole session, each costing a Unity fake-null check (a native interop call) every frame; and
  the dedup on Add was a **linear scan** — O(n) per district-build event at n=2,668, O(n²) over a session. Now a
  matched subset refiltered only on change, a prune, and an O(1) set that compares by **reference** (UnityEngine's
  `Equals` compares native pointers, so two *different* destroyed districts compare equal and a value-based set
  would collapse them). **Drilled in-game twice**, and `Update` moved by what the measurement predicted:
  `SelectorTile` 218.7 → **6.3 µs**, districts walked 2,668 → **1**, `Update` total 391 → **167 µs** (−224 µs against
  237 µs of measured scan); the log confirms the mechanism — *"matched 1 of 2669 tracked district(s)"*. **Read the
  bucket, not the total:** HAF's total went 612 → 671 µs across those runs and that is not a regression — the later
  scene had 46 live pawns against 18 and `PoseOurs` alone went 74 → 294 µs. That is the same trap that produced a
  wrong claim earlier the same day, and the reason the correction above exists. *Three process notes, all the same
  lesson in different clothes:* two of four mutations survived the first drill because the **tests** were weak, not
  the code (a "refresh only while empty" bug passed a test that started empty; a reference-vs-`Equals` mutation is
  invisible to a stand-in that doesn't override `Equals`); the **pre-push gate refused the push** when
  `DistrictScanTests` passed alone and failed in the full suite, because it and `BindStallTests` both reset District
  session state and xUnit runs classes in parallel — the second isolation bug of the day from that root; and the
  bucket that had been dismissed as "diffuse" for two passes turned out to be a list nobody was pruning, which is
  what recording a *verdict* instead of a *measurement* costs. **Still open:** the per-match work reads ~6 µs steady
  but **~497 µs during the load window**, visible only now that the scan no longer hides it.

- **`SelectorTile` — THE MEASUREMENT BEFORE THE FIX (2026-08-23).** At 219 µs/frame it is **36% of HAF's entire
  per-frame cost** and the largest unexplained number in the runtime. It has now been looked at three times: 08-21
  called it *"diffuse per-district overhead, left as is (0.6%)"*; 08-23 accounted for **~9 µs** of it (the Fx-tree
  walk re-resolving every field on every visit) and left **~210 µs** unattributed. Ruled out by reading this round:
  every per-loop diagnostic — `DumpPlbcLevers`, `DumpAllChannels`, `DumpGroundMatchers`, `DumpSelectorElements`,
  `DumpNativeGroundCandidates` — is correctly `DistrictDebug`-gated **and** latched, and `ResolveMainLayer` is
  cached, so none of them contribute. The existing buckets already said it is the loop's own head (`SelTileLoop` ≈
  `SelectorTile`; bind/albedo/flat never reach the top six). What they could **not** say is the thing that decides
  the fix: the loop walks **every district the game presents** to find the one or two that are ours, so 219 µs is
  either many cheap skips (fix: keep a matched subset and stop walking the rest — a skip still pays a Unity
  fake-null check, which is a native interop call, not a reference test) or a few expensive matches (fix: the
  per-match work). `SelTileSkip`/`SelTileOurs` now split it, and their **call counts are the district counts** — a
  number HAF has never printed: `districts 47 skipped 47 µs (1000 ns ea), 1 ours 180 µs (180000 ns ea)`, silent when
  the district axis isn't running. The ours-timer closes in a `finally` because that body has **six `continue`
  paths**, and an `End()` they skipped would under-count time *and* calls — the same accounting leak the 08-22
  `Update` fix closed. **Deliberately no fix yet**: picking one without the numbers is exactly what produced the
  "diffuse" verdict that turned out to be partly wrong. Two tests pin the summary segment (and its silence),
  drilled by removing it. See [Performance.md](docs/Performance.md) §6.

- **BACKUPS GOT SMART, AND STOPPED FAILING IN SILENCE (2026-08-23, editor side).** Found by accident while checking
  whether the offsite copy existed: the *Offsite folder* had been renamed hours earlier, the window displayed the
  dead path as if it were fine, and `OffsiteZipCore` opened with an unconditional `Directory.CreateDirectory`. The
  next backup would have **silently recreated a local folder nothing syncs** and reported *"Offsite: zipped N files
  → …"* — a completely successful-looking line about a backup that was no longer offsite in any sense.
  `SnapshotInto` had the same hole for the backup **root**: creating the new *timestamped* folder is correct,
  creating its *parent* is not — a renamed root or an unmounted drive rebuilt the chain and started a fresh empty
  history while every real snapshot sat under the old path and the *Existing backups* list simply went empty. Both
  now **refuse** and name the path and the likely cause, and both warn **in the window** the moment the path is
  missing, because a refusal that only reaches the status line after a run is no use when the daily auto is
  unattended. *(Drilled: with the path left stale deliberately, a full backup wrote its local snapshot and created
  nothing — `D:\Backup\Compressed` stayed absent.)*
  **Then the size problem, measured rather than assumed:** two snapshots 3.5 hours apart differed in **18 of 4,076
  files** — every backup re-copied ~1.4 GB of which ~99.6% was byte-identical, `source` alone (the licensed models)
  being 971 MB of it. Locally, unchanged files are now **hard-linked** to the newest snapshot: zero extra bytes,
  each snapshot still a complete independently-restorable folder, the Time Machine / `rsync --link-dest` trick.
  First real run: **4,077 files, 3,966 hard-linked (1.2 GB saved), 111 copied (65.4 MB)** — verified by link count
  (`stat %h = 2`), not by the report, because `du` on Windows cannot see NTFS hard links and its "saved" arithmetic
  would have been self-confirming. Offsite, an unchanged snapshot is now **not uploaded at all**: a SHA-1 over every
  file's path, size and mtime is written into the snapshot and beside each zip, and a match means the existing zip
  already IS this backup. Not a hash of the bytes on purpose — reading 1.4 GB to decide whether to upload 1 GB is a
  poor trade, and it is the same evidence the copy step already trusts. An absent or unreadable signature always
  proceeds: **"I don't know" must never be read as "unchanged"**, and the sidecar is written only after the zip is
  verified and moved, so a crash mid-zip cannot leave a signature claiming an upload that never happened. Safe
  because nothing writes INTO a snapshot — restore copies *out*, the delete-guard copies *in* from the live tree —
  and a failed link falls back to a plain copy: costs space, never correctness. See [Backup.md](docs/Backup.md).
  **DRILLED 2026-08-23, both directions.** Restoring: a real hard-linked file (`links=2`) copied out of a snapshot
  and then edited left **both** snapshots byte-identical and the link count at 2 — the restore path is safe as
  documented. The hazard: writing INTO a hard-linked file really does change every name sharing it, and deleting one
  name really is safe, confirmed on scratch files. *The first attempt at that negative control silently failed to
  create the link and reported "hazard NOT real" — the same false-negative shape as a mutation that never applied,
  the third time in one day. Redone with the link creation verified first.* And the drill found a real defect nothing
  else would have: `fsutil hardlink list` showed the 12:40 snapshot sharing bytes with the **09:11 `_auto_`**
  snapshot rather than the 12:27 manual one beside it — `PreviousSnapshot` ordered by NAME, and `_auto_` starts with
  `_` (0x5F), which sorts after every digit. Never wrong (any previous snapshot is a valid link base) but wasteful:
  it linked against a 3.5-hour-old base and copied 111 files where ~18 had changed. Now ordered by time.
  **Follow-up the same day — the local root was 17 GB and 6.4 GB of it was junk.** 362 delete-guard folders, almost
  all bake-test fixtures the guard had faithfully snapshotted: every suite run bakes assets under a throwaway prefix
  and deletes them again, and `__smoketest__ReconDrone` alone was 1.9 GB as eight copies of one 232 MB fixture. The
  restorable list had become a wall of `__convgate__` entries, which is the real cost — *a safety net nobody can see
  into is not a safety net*. The guard now excludes test fixtures the same way it already excluded preview scratch
  (*it protects what cannot be rebuilt, not what rebuilds itself*), referencing each suite's **own prefix constant**
  rather than a copy, because a duplicated literal would drift silently the day a suite renames its fixtures. And the
  layer finally has retention — it was the only one without any — pruning by **age, not count**, since a burst of
  thirty deletions in one afternoon must not evict the single one from yesterday that someone actually needs.
  Configurable in the window (default 14 days, **0 = keep forever** for anyone relying on the original promise);
  manual backups, `_prerestore` and Factory `_removed_` undo snapshots are still never auto-deleted.

- **ONE THROWING POLL NO LONGER DISABLES EVERY POLL AFTER IT (2026-08-23).** The 08-22 fix put `Plugin.Update`'s
  fan-out in a `try/finally` so the frame accounting always closed — but the `try` still wrapped **all ~25 polls**,
  and the catch's own wording admitted the rest: *"the rest of this frame's polls were skipped."* A poll that threw
  took every poll after it down, that frame and every frame it kept throwing, so a persistently failing
  `TickTexture` silently disabled `BattleTurn`, `FacingPersist` and `Formation` — subsystems with nothing to do with
  textures — behind one once-per-message warning that had scrolled away hours earlier. Each step now runs in its own
  `Poll(bucket, name, run)` guard: timed into its bucket, its failure named and counted **against its own site** so
  the smoke report's error list says *which* subsystem broke, and its neighbours run regardless. The delegates are
  cached in `readonly` statics rather than converted at the call site, because a method-group conversion at 25 call
  sites allocates an `Action` per poll per frame — the kind of cost this file exists to measure rather than assume.
  Granularity is the BUCKET, not the individual call (the three dial polls still share `Dials`), because the bug is
  cross-SUBSYSTEM silence and splitting them would change what the FrameCost buckets mean. The outer catch survives
  as a backstop for the fan-out itself and now says so. 6 tests, drilled by making `Poll` propagate again (4 caught).
  **DRILLED IN-GAME 2026-08-23** (build `08:54 UTC`): 0 `per-frame poll … threw`, 0 fan-out throws, every bucket
  still populated (`SelectorTile` 218.7, `PoseVanilla` 103.5, `PoseOurs` 74.9, `AnimStates` 45.6, `EngineAudio`
  40.2 µs), smoke PASS, 0 injection errors — and `Update` at 391 µs against 396 µs before, so the 25 guards and
  their cached delegates cost nothing measurable.

- **A MALFORMED CHARACTER NO LONGER DELETES EVERY CUSTOM DISTRICT (2026-08-23).** The twin of the pack-`modId`
  crash, in the sibling that was overlooked. `haf_districts.json` was parsed by one `JObject.Parse` inside one try,
  with the per-entry loop inside it too: a **single malformed character** left `distModels` empty and every custom
  district silently gone behind one LogError, and a single bad **entry** aborted the loop and took every entry after
  it as well. The model registry has had a field-by-field regex fallback for exactly this since it shipped
  (`ParseModels`); its twin never got one — the recurring shape of this codebase is a fix that lands in one of two
  twins. Now three layers, matching `ParseModels`: the primary object parse, **per-entry isolation** so one bad
  entry is skipped loudly instead of sinking its neighbours, and a **regex fallback** when the document itself will
  not parse. Every recovered value goes through the same converters and the same accept/reject gate (`Usable`) as a
  parsed one, so the fallback cannot smuggle in something the primary path would have rejected. 12 tests including a
  **parity oracle** — the two extractors compared field-for-field on one document, which is the only thing that pins
  the fallback's index alignment. *Two drill lessons, both of them the same shape as the gate that passed while
  blind:* the first fixtures were built with `.Replace("}\n          ]", …)`, which matched **nothing**, because a
  verbatim string in a CRLF file contains `\r\n` — the "broken" document was byte-identical to the valid one, so
  the whole regex fallback could be deleted with the suite still green. And `ParseDistricts` filters through
  `Usable`, whose GUID check needs the live game, so assertions on it were vacuous outside it; the raw seam
  (`ParseDistrictsRaw`) is where the primary-or-fallback decision is observable. Both fixtures now have a test of
  their own asserting they really are malformed. Five mutations drilled, all caught.

- **A DISTRICT THAT NEVER BINDS NO LONGER FAILS IN SILENCE (2026-08-23).** The scoped poll retries the building-element
  bind about once a second for as long as a district is unbound, and that retry is right — selectors load
  asynchronously, so early failure is normal. Everything around it was wrong, in two ways that compounded.
  *(1) The one-shot log key was the REASON, not the DISTRICT.* `bindLog.Add("notgt")` / `Add("nodonor")` share one
  session-scoped set across every district, so the FIRST district to stall claimed the key and every other district
  was permanently silent for that reason — with two districts, one masks the other entirely. *(2) It was
  `Plugin.Diag`*, gated behind `VerboseLog`, which is **off by default**. Together: at default settings a district
  could fail to render for an entire session and emit **nothing, at any severity, no matter how long it went on** —
  which is exactly what made the retry loop invisible while it was also burning ~580 uncached reflection lookups a
  second (see the Fx-tree entry above). Fixing the cost first is what made fixing the silence urgent: the symptom
  that would eventually have exposed it is now gone. The key is `(district, reason)` so each district speaks for
  itself; a stall that outlives "still loading" escalates **exactly once** to a real `LogWarning` naming the
  district, the reason and the consequence (*"its custom visual will NOT render"*); and the counter resets on a
  late bind so it never carries into a re-arm. **The retry itself is unchanged and still never gives up** —
  fail-soft stands, per the pre-flight rule; what changed is that it stopped being mute. Threshold is a named
  constant (`BindEscalateAfter = 30`, ~30 s at the poll's 1/s) behind a pure `ShouldEscalateBind`, which fires on
  the Nth attempt rather than every attempt past it — an already-unrecoverable stall must not become log spam.
  8 tests, three mutations drilled: re-keying the counter globally (3 caught), escalating on every attempt past N
  (2), and disabling the escalation entirely (4). *Note on that third one: the first harness run reported it
  SURVIVED — the perl expression had silently failed to apply. A mutation drill that does not verify the mutation
  landed reports "your tests are weak" when the truth is "my drill missed", which is the same failure shape as the
  catalog gate that passed by no longer seeing its inputs. Re-run with the edit verified, it fails 4 tests.*

- **TWO LINKS WRITING ONE FORMATION NAME ARE NOW DETECTED (2026-08-23).** Investigating the seven `[Formation]`
  warnings in a clean load found `'Formation_1'` warning **twice**. Cause: three registry links target that name
  and two of them carry dummy data, so each performed its own in-place overwrite.
  The immediate defect was in what the log SAID — `created` only ever remembered formations HAF **injected**, never
  ones it **overwrote**, so a repeat write looked like a first write and re-emitted a warning whose text blames a
  *vanilla* collision (*"If that name is a vanilla formation…"*) for what is really a collision inside the author's
  own registry. The latent one is worse: formation data lives in the database **under a name**, so every link on
  that name resolves to the same object and **the last write wins for all of them**. Verified harmless in the
  shipped pack — both writers put a single dummy at the origin, byte-identical — but nothing checked, so the day
  they diverge one unit silently inherits the other's layout with no signal but two warnings the author has already
  learned to ignore. Now: `FormationSignature` derives a comparison key from exactly the four things
  `FillFormationFields` writes — the **scaled** dummy positions (so `layoutScale`, and the `scale` it falls back
  to, are seen through), the per-orientation coordinates, the six `ColumnsCountPerRow` arrays and the low-spec
  reference — so it cannot drift from the write it describes. `ReportFormationCollisions` runs at **parse**, before
  anything has been written: identical data is a Diag, differing data is an **ERROR naming both links** and saying
  which way the clobber goes. At apply time the write itself is **unchanged and unconditional** — only the log line
  now distinguishes a first overwrite (warns, as before), an identical repeat (Diag — the duplicate alarming line
  is gone) and a differing repeat (error). A pure repoint carries no data and can never collide, so the shipped
  pure-repoint link is correctly silent. 12 tests, four mutations drilled: dropping `layoutScale` from the signature
  (2 caught), un-skipping pure repoints (1), never reporting a conflict (3), dropping `lowSpec` (1).

- **THE FX-TREE WALK STOPPED RE-RESOLVING EVERY FIELD, EVERY VISIT (2026-08-23).** Investigating "those HarmonyX
  warnings in the log" found they were not noise: **23,194 of the log's 37,329 lines — 94% — were
  `AccessTools.Field: Could not find field`**, and they came from `CollectLeaves`, the recursive Fx-material walk.
  The walk is POLYMORPHIC: it probes every node for both emitter and selector shapes, so each node necessarily
  MISSES the ~4 probes for the shape it isn't. Every miss was an uncached type-hierarchy walk plus a formatted log
  write, and with ~121 emitter nodes in the tree and a bind that retries once a second while a district is unbound,
  that ran forever. The tell was one line above the offender: `GF` — `// no AccessTools warning-on-miss (probing
  spams the log)` — exists precisely to prevent this, and `CollectLeaves`, defined immediately beneath it, used GF
  for probe 1 and `AccessTools.Field` for probes 2-9. The migration stopped one line in. **The fix is NOT a swap to
  `GF`:** GF is `Type.GetField`, which cannot see a PRIVATE field inherited from a base type, while
  `AccessTools.Field` walks the hierarchy — swapping would silently change which nodes the descent can reach, the
  one thing that must not move. New `GFA` memoizes `AccessTools.Field` instead: identical `FieldInfo`, one dict hit
  on a repeat, warning at most once per (type, member) per process. Applied to `CollectLeaves` and its twin
  `SetInstantAppear`. **Drilled in-game: 23,194 → 149 warnings (156×), log 37,329 → 779 lines**, and — the check
  that actually mattered — the descent resolves exactly what it did before: the Oracle still binds
  (`bound 1 building element(s) across 1 tile(s)`), its selector still lands on channel 0, the mesh footprint still
  finds its 2 elements, smoke still PASS, 0 injection errors. `SelectorTile`, previously the single most expensive
  HAF bucket at 227.7 µs/frame. **CORRECTED 2026-08-23:** the first draft of this entry said it "dropped out of the
  top six entirely (below 10.7 µs)". That was measured in a session with 3 injected models and 1 live pawn against
  the original's 19 and 18. The reasoning for quoting it anyway — that the district state string was identical in
  both (`2 district(s) [1 tile(s) live, 1 scoped]`) so the bucket was comparable — **did not hold**: a later heavy
  run put `SelectorTile` straight back at the top. Two runs reporting the same district state differed ~20× on it,
  so that string does not capture what drives the bucket, and it cannot be compared across unlike scenes.
  Like-for-like, both heavy scenes: HAF total 608 → 570 µs, `Update` 396 → 391 µs, `SelectorTile` 227.7 → 218.7 µs.
  The honest result is **~9 µs (~4%)**, not a collapse; `SelTileLoop` ≈ `SelectorTile` in every reading, so the bulk
  is the loop head, not the bind. What the change unambiguously fixed is the LOG — 23,194 → 149, re-confirmed at
  209 on the later heavy run — because the walk that produced those lines runs on the bind retry (~1/s), not per
  frame. Silencing it was worth far more to the log than to the frame, and the first draft did not say so.* **A gate lesson came with it:** adding `GFA` made
  `tools/check-catalog.sh` blind to those sites — its alternation ends `|GF)\(`, which `GFA(` does not match — and
  the gate still reported OK, because a shape it cannot see is a shape it stops counting rather than one it
  reports. Taught, and drilled both ways with a planted bogus name: the un-taught gate passes it, the taught gate
  fails and names the file and line. The script now says that any new accessor helper must be registered there.

- **EVERY OUTPUT NAMES THE BUILD THAT PRODUCED IT (2026-08-23).** The two fixes above were drilled in-game and came
  back **PASS** — F8 smoke green, 22 models loaded, 0 injection errors, and the editor's 43-test bake suite green
  too. The PASS was real and the code was fine, but it was **evidence about the wrong build**: the deployed
  `HumankindAssetFramework.dll` was dated `08-22 22:41`, from the previous day, and neither fix was in it. Nothing
  on screen, in `LogOutput.log`, in `haf_load_report.txt` or in `haf_smoke_report.txt` said which build was
  talking, so a stale-DLL drill was indistinguishable from a fresh one — the failure mode this project spent the
  whole day removing from *packs*, sitting in its own diagnostics. What gave it away was a header field that
  happened to be new (`schema implemented=`); without that coincidence the run would have been recorded as
  verification. Now `Plugin.VersionLine` — `HAF 0.1.0 (built 2026-08-23 07:30 UTC)` — leads the **F8 panel** (first
  line, above the binding banner, because the panel is what gets screenshotted as proof), the **boot log line**,
  and the header of **both** reports. The stamp is compiled in via an MSBuild `AssemblyMetadata` attribute rather
  than read from the DLL's file time, so it describes the CODE and survives every copy, deploy and backup restore;
  the file time remains as a fallback and *labels itself as one*, because a file time answers "when was this
  copied", not "what is in it". Deliberately non-deterministic: this assembly is a hand-deployed game plugin, never
  a cached build input. `Tests/BuildStampTests.cs` guards the csproj stanza — losing it would break no build and no
  other test, it would just quietly restore the ambiguity — drilled by stripping the attribute and watching the
  test fail through to the file-time fallback.

- **THE FIRST CUT OF THE WRAPPER GUARD WARNED ON THE REFERENCE PACK (2026-08-23, same day, caught by the drill).**
  Shipping the `modId` fix introduced a regression the unit tests could not see: `WrapperStr` warned on any key
  that was present-but-unusable, and **empty string is how the editor writes "not set"** — every pack it bakes
  carries `"module": ""` and `"moduleGuid": ""`. So ENC itself logged two warnings on every clean load. Behaviour
  was correct throughout (the folder-name auto-match resolved, `enc #1→ENCReload`); only the noise was wrong — but
  a warning that fires on the reference pack has stopped meaning anything by the second load, which is the same
  disease as the silence it replaced. The rule is now per-key: a **wrong type** (number, bool, object, array) warns
  on any key, because nothing emits `"modId": 3` on purpose; a **blank** warns only where nothing writes it blank
  by design — `modId`, where the fallback silently renames the pack to its file name and that name is the identity
  other packs write `dependsOn`/`overrides` against — and is a Diag line on `module`/`moduleGuid`, where blank *is*
  the editor's idiom. Pinned by a test that asserts the reference pack's exact wrapper shape logs **zero**
  warnings. That test needed two negative controls to be worth anything, and they earned their keep immediately:
  the first capture hooked `BepInEx.Logging.Logger.Listeners`, which a bare `ManualLogSource` is not attached to,
  so it recorded nothing and the "no warnings" assertion **could not fail**. The controls failed, the capture moved
  to the source's own `LogEvent`, and all four now bite.

- **`schemaVersion` DOES SOMETHING NOW (2026-08-23).** The same critical review found the registry's version field
  was decorative: parsed on both paths, printed into `haf_load_report.txt`, and **read back by nobody**. A pack
  could declare any version at all and load identically. The README calls the registry "the public API other mods
  build against", which makes an unread version field worse than no field — it tells a pack author they are
  protected against a skew that is in fact entirely unchecked. The decision the fix needed turned out to be already
  made and already written down: [Multi-Mod.md](docs/Multi-Mod.md) has documented the contract since the pack
  format shipped — *"Currently `1`. Evolves **additively** — new keys are added, old files keep loading"* — so the
  work was to **implement the documented contract**, not to invent one. And additive evolution is what makes
  *refusal* the wrong lever: a pack from the future is a pack whose extra keys this build strips
  (`registryConfigKeys`) and whose known keys it reads exactly as the author intended, so refusing it would break
  something that demonstrably works to protect an author who would far rather a dial degrade than lose the whole
  unit. What was missing was never enforcement — it was ever **saying so**. Now: `Haf.Schema.HafSchema` owns the
  number (`Version` / `MinReadable` / the `Unversioned` sentinel) in the project the editor and plugin already
  share; `CheckSchema` classifies each surviving pack against it and returns **null for the ordinary in-range
  pack**, so the report keeps talking about conflicts instead of restating a number it already prints. A pack from
  the future **warns** — naming the consequence (*"keys introduced after schema 1 are stripped and IGNORED, so
  dials the author set may silently do nothing"*) and the remedy (*"Update HAF"*); an unversioned legacy pack gets
  a quiet note, not a warning; a version below the floor warns to re-bake (the reserved lever for the day a
  field's *meaning* changes — the one break the additive contract does not cover). The implemented version now
  prints in the load-report header, `schema implemented=1 (reads 1+)`, directly above each pack's own
  `schemaVersion=` line, because that side-by-side is the comparison a modder is actually making when they open
  the file. **Fail-soft is pinned by test**, not just by intent: a theory over `{0, 1, 99, -5}` asserts no verdict
  can ever cost a pack its place, so turning the advisory into a gate fails the build. 18 tests in
  `Tests/SchemaVersionTests.cs`, including both parse paths (an advisory that fired only on the Newtonsoft path
  would miss the hand-written pack — exactly the one most likely to target a newer HAF). Three mutations drilled:
  the off-by-one that warns on the current version (3 caught), legacy packs falling through to the floor (2), and
  the future advisory going silent again (6). The number is also quoted to pack authors in two docs, so
  `tools/check-docs.sh` now fails the push if either drifts from the constant — drilled in all three directions.
  *Known residue, both in [Review-Backlog.md](docs/Review-Backlog.md):* ENCReload's `ModelRegistry.cs` still holds
  a **fourth** copy of the number as a literal that no guard compares, and the warning cannot yet name *which*
  dials are ignored — the stripped-key set is computed but a real pack carries ~56 legitimate bake-time editor
  keys that would bury the two that matter.

- **ONE BROKEN THIRD-PARTY PACK NO LONGER SINKS EVERY OTHER PACK (2026-08-23).** A critical review found, and a
  drill confirmed, that `"modId": null` in any `pack.json` disabled **all** custom content for the session — the
  reference pack included. The mechanism: `(string)root["modId"]` returns C# **null** for a JSON null *without
  throwing*, so it slipped past `ParsePack`'s catch and reached `ResolvePacks`' duplicate-id `Dictionary` as a null
  **key**. `TryGetValue(null)` is an `ArgumentNullException`, and its only handler resets `entries` and latches
  `loaded` after three tries. The failure was also close to undiagnosable: `WriteLoadReport` sits past the throw, so
  `haf_load_report.txt` was never written, and the log carried a bare stack trace naming **no file**. The
  boot pre-flight could not help either — it runs after registration, which never happened — and `PackValidator`
  has no rules for wrapper metadata at all, only for model entries. The tell was that the **regex recovery** path
  had guarded this exact shape since it was written (`.Groups[1].Value.Length > 0`) while the primary path never
  did: the fallback was more defensive than the thing it backed up. Fixed in three layers. *(1) At the source:*
  `WrapperStr` accepts a wrapper string only when it is really a non-blank string, so `modId` / `module` /
  `moduleGuid` that are JSON-null, numeric, object, array or blank keep the computed file-name default **and warn**
  — silently discarding what an author explicitly wrote is the failure mode this path exists to stop. `ParsePack`
  also gained a post-condition: no `Pack` leaves it without a usable id, even from a file literally named `.json`.
  *(2) Defence in depth:* `ResolvePacks` is the pure, separately-tested entry point, so it now treats an unusable id
  as any other broken pack — **skipped, loudly, by name** — instead of throwing. The blast radius of a broken pack
  is that pack. *(3) Diagnosability:* the load-failure log line now carries the discovered pack list, so an
  unanticipated throw still hands the modder a candidate set to bisect. Two adjacent bugs of the same family fell
  out: a non-integer `schemaVersion` used to **throw on the cast**, dropping the whole header into regex recovery
  and logging "header didn't JSON-parse" against a file whose JSON was perfectly well-formed; and ids are now
  trimmed on *both* sides of every reference (`modId`, `dependsOn`, `loadAfter`, `overrides`, in the primary path
  **and** its regex twin) — trimming one side only would have turned a harmless trailing-space typo into a
  dependant that resolved today and was skipped tomorrow. 20 tests in `Tests/PackWrapperMetadataTests.cs`,
  mutation-drilled: **15 of the 20 fail against the pre-fix code**, and the 5 that pass either way are the
  deliberate no-regression guards.

- **THE LAST THREE FROM THE 08-22 REVIEW (2026-08-22).** *(1) The fourth hand-maintained field list is now gated.*
  A clone owns no assets until its own bake, so every `int[4]` GUID on the copy must be reset — an inherited one
  silently points the clone at the **source's** ClipCollection, which is how `clipIdleAlt2` shipped. That fix
  carried a good comment and nothing else; three lists of exactly this shape were already gated (the two ownership
  rebases, the recipe round-trip) and this one was not. `check_handlists.sh` now compares `ModelDef`'s 11 `int[]`
  fields against the Clone block on every push, and the drill re-removes `clipIdleAlt2` to watch it named and
  failed. *(2) `Plugin.Update` closes its accounting even when a poll throws.* Unity catches per-message, so a
  throwing poll skipped the whole tail of the method — including the two lines that close the frame. The Update
  bucket lost that frame while its sub-buckets kept their ticks, and `frames` stopped incrementing while the window
  kept aging, so fps read low, `frameUs` inflated, and the percentage collapsed toward zero: **the meter read its
  healthiest exactly when HAF was most broken.** `try/finally` fixes the reporting; the `catch` names the failure
  once (and marks an injection-error site) instead of letting Unity's per-frame spam bury its own first line.
  *(3) The meter's advertised scope now matches its real one.* `Performance.md` claimed "everything HAF did in an
  average frame"; the 33 timing sites cover the `Update` fan-out, the pose hook and the district path — not the
  other ~36 Harmony hooks, not `OnGUI` (which walks the GPU budget by reflection every repaint while the panel is
  open), not Harmony's own dispatch, and not GC from HAF's allocations. The page states all of that, plus the fact
  that a 5-second **mean** hides hitches, so the meter is read as a budget tool rather than a hitch detector.
  *(The review's fourth item — CI running three of five checks — was already closed earlier the same day when the
  hot-path and catalog guards moved onto the runner.)*

- **TWO EDITOR FIXES FROM THE 08-22 REVIEW (2026-08-22).** *(1) The gun and trail dials were invisible on exactly
  the sources they exist for.* Eight sites in the Vehicle Lab's Deploy/gun/recoil block counted roles from the raw
  `parts` list while every other section used `ActiveParts` and Generate used `fast ? boneParts : parts`. So on a
  rigged source with the fast path on — the Ehrhardt, any `SKM_` rip — marking a bone **Trail** or **Gun** left the
  section reading *"no trails marked"* with every control disabled (Spread, Deploy frames, Gun pivot, Gun raise,
  Recoil, lead-in), while Generate read those same bone roles and shipped the **defaults** (35°, pivot 0.5, recoil
  0) with no way to dial them. Root cause was two copies of one predicate: the UI had
  `useSourceRig && boneParts.Count > 0` inline, Generate had its own `fast`. Both now come from `FastPath` and
  Generate takes `ActiveParts` itself, so the list the UI counts and the list the rigger consumes cannot disagree
  again. *(2) The bake-test report printed PASS when nothing ran.* The verdict read `fail == 0 ? PASS : FAIL`, so
  an all-skipped run wrote *"PASS — 0 passed, 0 failed, 1 skipped"* into the window headline and into
  `Logs/haf_bake_tests_report.txt` — the artifact whose entire job is answering "did the tests pass before this
  release?" — reachable on any machine without Blender. The per-row label already said SKIPPED for a zero-pass
  section; the summary never learned the same rule (mine, from Thursday). It now reads **NOTHING VERIFIED**, and
  the Console line becomes a warning in that case, so a run that checked nothing cannot look like success anywhere.

- **THE GUARD WAS REAL; IT JUST WASN'T ON EVERY DOOR (2026-08-22, review finding — fixed).** Saturday's commit
  closed a data-loss hole: type a resource name that already exists and Bake and Save settings grey out behind a
  red "Not allowed" box. `ModelRegistry.Upsert` is a blind `RemoveAll(name) + Add`, so a write under someone
  else's name deletes them and orphans their baked assets. Three of the Factory's four write paths asked
  `BlockedByRenameClobber` first — **`Make static…` did not**, and its button sat in no disabled scope either, so
  with the warning visibly on screen one click destroyed the colliding entry. Rather than bolt a fourth
  hand-check on (the habit that produced the gap), the guard became the single definition and grew the shape it
  had always missed: a **＜new model＞ typed straight onto an existing name**, which the rename test could never
  see because there is no previous key to compare against — the same hole, one door along. It now runs *before*
  the confirm dialog, because being told a write would destroy another entry beats confirming an action that is
  about to be refused. **And the guard's own comparison exposed a third gap**: it compares `OrdinalIgnoreCase`
  while `Upsert` removed with ordinal `==`, so renaming `Tank` → `tank` read as "writing over itself", then
  failed to remove the old row — two registry entries whose baked assets (`<name>_Skeleton.asset`, …) are the
  *same files* on Windows, each bake silently overwriting the other. `Upsert` now removes case-insensitively;
  verified first that no shipped pack has case-duplicate names, so nothing is merged by the change.

- **THE ONE HOLD THAT COULD STALL AN ATTACK NOW FAILS OPEN (2026-08-22, review finding — fixed).**
  `Hk_BattleHoldFire` can *suppress* a game action, and its bound came from a field read by reflection. The test
  was `ct == null || elapsed < deadline` — and `GetMember` returns null on **any** resolution failure (a rename, a
  moved base class, a throwing getter). A null therefore selected the **hold** branch, un-latched `isReadyToStart`
  and returned false *every frame with no deadline at all*: the ranged attack never starts and the choreography
  action never completes. Every sibling hold in this file fails **open** by explicit policy — "any failure =
  vanilla, never a stuck army" — and `TargetMethod` already refuses to patch when the un-latch field is missing;
  the single input deciding whether this hold was bounded was the exception. An unreadable clock is now treated
  exactly like an expired one: release, plus a one-shot warning naming the likely cause and pointing at the
  bindings report, so an incompatibility is loud instead of a frozen battle. The decision moved into a pure
  `TryElapsedSince(clock, now, out seconds)` — `now` is a parameter rather than `Time.time`, which is what makes
  it testable outside the engine (the first cut called `Time.time` directly and the test host rejected the ECall,
  a useful reminder that "pure" means *no ambient inputs*, not just "no side effects"). Four tests, including a
  non-numeric clock, mutation-drilled: restoring the fail-closed behaviour fails the one named for the regression.
  Mitigating context, unchanged: the path is off by default and `creationTime` is catalogued, so a rename also
  shows up in the bindings report at Awake.

- **THE SESSION FENCE WAS DRAWN BY AN IMPLEMENTATION DETAIL (2026-08-22, review finding — fixed).** The
  `[SessionScoped]` rule policed a field only if its type *happened* to have a public parameterless `Clear()`.
  On net471 that silently excludes `ConcurrentQueue<T>`, `ConcurrentBag<T>`, arrays and `ConditionalWeakTable` —
  by the review's measurement **137 of 549** author-written statics were inside the fence, and the boundary was
  an accident of the BCL rather than a statement about lifetime. It had already cost a real bug:
  **`fireGuidQueue`** is a `ConcurrentQueue` written from the **sim thread**, and the re-arm sweep that clears
  every neighbouring per-entry collection walked straight past it — so a strike enqueued in the last frame of one
  session survived into the next, where unit GUIDs restart from zero, and armed *the wrong unit's* recoil. The
  rule is now "state shaped like session state", with a clearer per shape: `Clear()` as before, queues and bags
  **drained** via `TryDequeue`/`TryTake`, arrays zeroed via `Array.Clear`, and `ConditionalWeakTable` — which
  cannot be emptied in place — forced to declare `[ProcessLived]` or `[SessionScoped(Manual=…)]` instead of
  passing unseen. Widening it immediately flagged **27 undeclared statics**, each now annotated with its real
  lifetime (literal lookup tables, compiled-accessor tables, per-pass scratch, config-derived caches, the binding
  catalog). Scalars stay outside deliberately — a static `bool` can be a constant, a cache or a per-session latch,
  and shape cannot tell them apart — so `UnpolicedStaticCount()` **reports** how many remain, making the fence's
  edge a number in the test output rather than an implied "everything is covered". Four tests, mutation-drilled:
  restoring the old predicate fails the one that pins the shapes. The catalog gate then caught the fix's *own*
  new reflection (`TryDequeue`/`TryTake`), which is the two guards checking each other exactly as intended.

- **THE DROPPED GUARD, AND THE ORACLE THAT SHOULD HAVE CAUGHT IT (2026-08-22, review finding — fixed).** The
  pre-extraction tuning parser read `if (float.TryParse(…, out float sv) && sv > 0f)`. The extraction into
  `PackTuning.Parse` kept everything except **`&& sv > 0f`**, and nothing downstream re-guards it: `Inject.cs`
  multiplies the value as given. A hand-edited `"scale": 0` — a plausible way to write "disable this rule" —
  multiplies a **shared** GPU mesh-table entry by zero, so the unit and anything sharing that mesh index collapse
  to a point and are culled; the recorded probe then becomes the zero vector and every later pass computes
  `0/0 = NaN` and re-applies for the rest of the session. `-1` inverts the mesh through the origin and renders it
  inside-out. Both silent, in a framework whose stated rule is that nothing is silently disarmed. The guard is
  back — written `!(sv > 0f)` so **NaN is rejected too** — and it is no longer silent: it emits a warning naming
  the pack, the key and the value, because restoring the old quiet skip would still leave an author wondering why
  their edit did nothing. No shipped pack is affected (the one live rule is `Biremes ×4.0`). **The real fix is the
  oracle**: `PackTuningLegacyParityTests` keeps the pre-extraction loop verbatim and compares the new parser
  against it over a 19-entry corpus — healthy packs, hand-edit shapes, and the half-saved truncations a live edit
  passes through. That is exactly what `DialConfig` and `PoseMath` each shipped, and each of those oracles caught a
  real divergence; this extraction shipped none, which is why its divergence reached the registry. Mutation-drilled
  by re-introducing the bug: **6 tests fail**, three of them naming the corpus entry (`Zero`, `Neg`, `NegSmall`).

- **THE CATALOG GATE WAS BLIND TO 46 OF ITS OWN SITES — AND ONE OF THEM WAS THE MEMBER THE LAST REVIEW NAMED
  (2026-08-22).** A7's promise is that the catalog covers the code, *proven mechanically*. Its extraction regex
  listed `GetMember`/`SetMember`/`CallMethod`/`FastMember`/`AccessTools.*`/`Traverse.*` — and not `CachedField(`
  or `GF(`, the district axis's own probe, 16 sites. Teaching it that family took the visible surface from 283 to
  311 literals and **failed immediately on 19 real uncatalogued names**. Then a second blind spot: `\([^)]*?"…"`
  cannot cross a `)`, so in `GetMember(GetMember(x, "Inner"), "Outer")` only *Inner* was ever extracted. A narrow
  second pass for that shape (accessor → identifier-call → literal; deliberately unable to wander into a
  neighbouring call) brought it to **329 literals and 13 more missing names**. Among them: **`FacingAngleOffset`**
  — the battle-aim member the 08-21 review explicitly listed as uncatalogued. It had never been added. The only
  occurrence in `GameBinding.cs` was a *comment describing the review that found it*, and because its read is a
  nested call, nothing could contradict that comment. It and `TagAsAbilities` are now real catalog entries on
  `PresentationUnitDefinition` / `SimulationUnitDefinition`, **validated against the game DLLs by bindcheck**
  (132/132 types, 0 missing). The remaining 30 are duck-typed reads over types resolved at runtime
  (`mat.GetType()`, `voBox.GetType()`) which the catalog's member→declaring-type model genuinely cannot express;
  each is site-scoped in the allowlist **with its reason**, diagnostics separated from functional reads, and the
  functional ones' residual silent-degradation risk written down rather than hidden by a gate that could not see
  the site at all. Drilled both new paths: a planted `CachedField(t, "…")` and a planted nested literal are each
  caught and named with file:line; before today both printed OK.

- **A BROKEN REPOINT NOW FAILS INSTEAD OF INFORMING (2026-08-22, review finding — fixed).** `UninjectedReason`
  returned *"its addon loaded but the repoint did not run"* with `mismatch = false`, so the entry landed in
  `Uninjected` — a list the verdict only ever **prints**. That string means the game loaded the unit, our
  `pawnDescription` matched it, and the repoint still didn't happen: a definite pipeline break under a green
  verdict, and the same bug class as the `_DRILL` suffix that shipped in the pack two days earlier — which a
  person caught, not the check. Failing it required fixing the **second half of the same defect** first: the
  reason was computed with a plain substring test while the injector picks the **longest** match, so an entry
  legitimately shadowed by a more specific one (a variant beating the base it extends) got the identical
  accusing line although it is perfectly healthy. The check now asks the injector's own question — it runs
  `LongestMatch` over the real entry list — and the two cases separate cleanly: *shadowed* names the winning
  entry and stays informational, while *"is the most specific match and still did not repoint"* raises
  `mismatch` and **fails** the smoke. The back-compat overload without the entry list keeps the old
  conservative wording and fails nothing. Five tests, mutation-drilled: neutering the flag fails the two named
  for it, and the shadowing test is the one that would have caught a naive fix turning healthy variants into
  false alarms.

- **THE DETECTOR THAT COULD NOT SEE ITS OWN DEATH (2026-08-22, review finding — fixed).** The smoke's live-pawn
  checks — skeleton truth and pose-hook liveness, the ones that prove the engine is actually rendering our models —
  read `knownManagers`, a list written at exactly **one** place in the codebase: inside the pose hook's pawn-added
  path, after two early returns. So the detector was fed by the very hook it certifies. A pose hook that never ran
  at all (patch failed to apply after a game update, its reflection broke, or the master toggle is off) left the
  list empty, examined zero pawns, and **passed** — with the coverage clause *omitted from the summary* rather than
  printed as zero, so the line read clean. It could only ever catch a hook alive enough to register a manager but
  no longer matching one entry. The fix gives it an **independent oracle**: `CountLiveArmies()` reads the live army
  count straight from the presentation entity factory, a surface no HAF hook writes. Zero managers while armies are
  live and entries are injected is now a **FAIL** that names the consequence ("every per-frame offset — pose,
  muzzle, elevation, turn ease — is silently dead"), because the pawn-added path must have run. The benign shapes
  are separated and stated rather than hidden: no armies at all, or managers registered but no slot carrying one of
  our descriptor ids, each produce a NOTE and a printed `0 live pawn(s) examined [N pawn manager(s), M army(ies)
  live]`. An unreadable oracle returns -1 and can never masquerade as a confident zero. Five tests
  (`SmokeVerdictTests`), mutation-drilled: neutering the rule fails the one named for it.

- **THE SUITE STOPPED BAKING THE SAME MODELS TWICE — 6.7 → 4.7 min, MEASURED (2026-08-22).** "Can't we parallelise
  it?" The honest answer was no: the bake path is 71 Unity API calls against 11 subprocess calls, and
  `AssetDatabase` is main-thread-only, so concurrent bakes are not on the table. But the run was doing the same
  work twice. With Everything selected, the whole-catalog row and the converted-rigs row each baked every
  `animated + convertRig` model — **12 of the run's 32 heavy bakes, and conversion bakes are the slowest kind**.
  The conversion row now also runs the catalog row's asset check on the bake it was already doing, so the catalog
  row hands those models over; same assertion, same outputs, one bake earlier, and the handed-over models are
  named in the report. The hand-over is conditional on both rows being in the same run — the catalog row alone
  still bakes everything, exactly as its title promises. **Verified in-game-adjacent (the user's own run): 6.7 min
  → 4.7 min, 43 passed / 0 failed / 2 skipped.** The pass count falls from 55 because twelve bakes are no longer
  double-counted, not because anything stopped being checked. Every row now also times itself, so the report
  finally answers "which row costs the time?" — converted rigs 1.5 min, whole catalog 1.3, golden snapshot 1.0,
  Blender+animation 0.5, synthetic cubes 0.3, control rig 0.1. Two measured negatives worth keeping: Blender's
  ~2 s process start is irreducible by `--factory-startup` / `--disable-autoexec` (tested, no gain), and the
  remaining big rows are exactly the ones the main thread pins.

- **THE GATE THAT PASSED ON WHAT IT GUARDS (2026-08-22, review finding — fixed).** `check-hot-path.sh` forbids
  SPIKE/EXPERIMENTAL labels inside `Plugin.Update()`, and the CHANGELOG claimed "the label can't outlive the
  experiment again without failing a push". It greps **case-sensitively**. Three calls in the shipped Update body
  carried lowercase `(spike)` — `PollTurnEase`, `PollTerrainHug` and `BattleTurn.Poll`, the last running for
  *every* unit — and the gate printed OK and exited 0 on all three. A guard reporting green on the very method it
  guards is the failure this project calls its worst, so the fix came with its own drill. Adding `-i` immediately
  produced a **false positive** on the line citing `docs/Wonder-Spike.md` — a doc link, not a label — so the match
  now requires the word to stand alone: ignored when glued to an identifier or filename (`Wonder-Spike`,
  `docs/Spike`, `de-spiked`, `…Spike.md`), still fired by every label form actually used (`(spike)`, `SPIKE:`,
  `EXPERIMENTAL (opt-in, …)`). Verified in both directions: it named exactly the three real labels, and after they
  were cleared it caught a freshly planted one. **The three were then PROMOTED, not deleted** — each is a
  documented, shipped feature with a live dial file, a `FrameCost` bucket and doc pages
  (`haf_turnease` / `haf_hugterrain` / `haf_battleturn`, peers of the untagged `haf_rotortrim` one line above);
  the labels were simply stale. And the two source-only guards — hot path and catalog surface — **moved into CI**
  beside the docs guard, for the reason that promotion was made in the first place: a pre-push hook is per-clone
  config, and `--no-verify` or a web edit walks straight past it.

- **THE SECOND SHOT (2026-08-22, review critical — fixed).** A critical review of the 109 commits since the
  08-21 pass found the turn-before-firing feature defeated on its *second* use. `TurnHoldForStrike` reuses an
  armed hold so that one strike's three prefixes (visuals + both schedules) share a single clock — the fix
  that stopped the bang desyncing from the recoil. But the marker it tested for "already armed" is the aim
  override, which **outlives its strike by design**: `SetAimOverride` writes a 120 s `until` because the same
  record doubles as the facing long-stop that keeps a unit pointed at what it shot. Existence was not
  pendency. So a second bombard from the same tile within two minutes — a siege, counter-battery, any
  consecutive turn — hit the early return, got `hold = 0` off a long-expired release time, and never reached
  the fall-through that arms a new bearing. Every consumer then agreed on the *previous* target's yaw: the
  attack pose fired at once, the recoil released at once, the elevation ramp had long since finished. The gun
  shot without turning, which is the exact symptom the whole 20-commit arc exists to eliminate. The drills
  never saw it because they fired **one shot**. The decision is now a pure predicate, `ArmedHoldPending`
  (`overrideFound && releaseAt > now`), with the reasoning at the call site and five tests in
  `Tests/StrikeHoldTests.cs` — mutation-drilled: reverting to the old test fails three of them, including the
  named regression. That closes the gap the review flagged in this subsystem specifically — it was the one
  place the project's own "extract the decision, test it" rule had never been applied, so a single in-game
  drill was its only oracle. **Provenance — nobody wrote this bug; two correct commits made it together.** The
  early return landed 2026-08-05 (`963042d`, the shared-clock fix) when the override's lifetime was **8–10 s**:
  at that lifetime "an override exists near this pawn" really was a sound proxy for "this strike is armed",
  because a hold is at most 3.5 s and the record expired seconds later. Twenty-one hours later `3ceb089` raised
  the lifetime to 120 s so facing would persist "until the game changes intent" instead of springing back on a
  timer — correct on its own terms, and it silently invalidated the proxy in the other file. Nothing in that
  diff touched the early return, because nothing had to: it still compiled, still passed, still read correctly.
  The bug was 16 days old and survived the 08-21 review; it is only visible if you ask what the *lifetime means*
  rather than what either commit does.

- **`ModelEntry`'S THREAD DISCIPLINE IS DECLARED, NOT MEMORISED (2026-08-22).** A review asked to reopen the A2
  god-object split, citing the two 08-21 data races as the "proven bug from the shape" that Decisions.md requires.
  **Neither race was in `ModelEntry`** — one was the reflection-cache *statics*, the other DistrictInject's collections
  — so the trigger wasn't met, and the quoted cost was off too (the four locked fields have **53** call sites between
  them, not ~200). But the underlying complaint was fair, and checking it found something worse than the review
  claimed: of `ModelEntry`'s **23 mutable collections, only 6 stated any thread discipline at all**; the other
  seventeen said nothing, on an object reachable from a simulation thread, guarded by exactly the kind of comment
  Architecture.md §2 already records as "a claim, not a guard" (both 08-21 races hid behind one).
  So: `Patches/ThreadDiscipline.cs` — every mutable field on `ModelEntry` declares `[MainThread("owner")]`,
  `[Locked("why")]` or `[Concurrent("why")]`, and `ModelEntryThreadTests` fails the build on an undeclared one.
  `[Concurrent]` is **machine-checked** against the field's real type (and the converse: a concurrent-typed field
  can't be filed as plain main-thread state), the four Architecture §2 names are pinned so a silent demotion fails,
  and a test asserts the inherited `Haf.Schema` half contributes no mutable collection — so "config is immutable" is
  true by construction, not by habit. Triage: **19 main-thread, 4 locked, 1 concurrent** (`fireGuidQueue`, the only
  `ModelEntry` field the off-thread hooks touch). Fault-injected both ways: an undeclared new field and a false
  `[Concurrent]` claim each fail by name. Architecture.md §2 now states the rule instead of listing four names; the
  split stays declined, on the record, with the reasons.

- **THE HOT PATH NO LONGER CALLS ITSELF A SPIKE (2026-08-22).** A review counted 52 `SPIKE`/`EXPERIMENTAL` markers and
  named five on the per-frame path: `PollWonderRows`, `PollDistrictMainRows`, `PollRepoDump` / `ProbeAxisGrowth`,
  `TickDistrictMeshSwap`. Checked, and the split matters: **32 of the 52 are in `Plugin.cs` as config-key
  documentation** — a description that warns a player "EXPERIMENTAL: this footprint mask is a work in progress" is
  honest labelling, not shipped-spike code — and one of the rest is a *war story* about a rendering artifact literally
  named "the spike plague". But the five call sites were real, and the principle is right: **a shipped plugin
  shouldn't run code its own comments call a spike.** What was actually true of each:
  - `PollWonderRows` — **promoted**. It is how a custom Artificial Wonder renders at all (the Oracle); config-gated
    and latched once every cell is filled. Label dropped, cost owned, `[District] WonderNativeRows` description
    rewritten (it no longer says SPIKE to the player).
  - `PollDistrictMainRows` — **promoted** to what it is: the shared-cell district path, the documented alternative to
    `DistrictSelectorTile`. Config-gated (blank = off), then 1-in-30 frames.
  - `PollRepoDump` / `ProbeAxisGrowth` — **already correct**, now said out loud: `[Debug]`-gated *and* one-shot
    latched, so they cost two bool reads a frame with the default config.
  - `TickDistrictMeshSwap` — **promoted**: it is the district axis's own per-frame driver (isolate path), shipped and
    documented in District-Visuals.md, no-op with no district entries.
  - `UseDeepClone` — **deleted**. A parked hack behind an always-false flag, plus the 45-line helper only that dead
    branch called: 46 lines of code the plugin shipped and never ran.
  - The **cost** half of the finding is history: the 1,350 µs/frame `WonderRows` was fixed in the 08-21 perf pass.
    Measured now, every one of these buckets sits below the FrameCost top-six cutoff (<35 µs on a 19-model save).
  - **`tools/check-hot-path.sh`** (new, in the push gate, fault-injected): `Plugin.Update()` may contain no `SPIKE`
    at all, and `EXPERIMENTAL` only when the same line names its gate (`EXPERIMENTAL (opt-in, [Props] …)`). The label
    can't outlive the experiment again without failing a push.

- **A7 — THE CATALOG'S GREEN LIGHT NOW MEANS WHAT IT SAYS (2026-08-21).** A review made a measurable claim: `bindcheck`
  can only validate what is *catalogued*, so `95/95 clean` is a statement about the catalog, not about the binding
  surface — and ~55 game-shaped member names were read by name and absent from it. Reproduced mechanically and it was
  **worse: 84**, including four the reviewer named by hand, each behind a silent catch on a functional path —
  `FacingAngleOffset` (battle aim), `IdleAudioEvent`, `CurrentTechnologicalEraIndex`, `BonesCount`. The A6 entry's
  "every non-diagnostic by-name site" was a hand sweep; it is corrected in place above.
  - **`tools/check-catalog.sh`** (new, in the push gate): extracts every string literal passed to a by-name reflection
    accessor, subtracts the catalog, subtracts an allowlist where **every entry carries its reason** (Unity/BCL names;
    four *tolerant probes* that already try several names and cope with all absent), and fails on the rest. Pure source
    analysis — no game needed. Fault-injected both ways on the day it was written: dropping `BonesCount` from the
    catalog and adding a new uncatalogued site were each caught by name, and the baseline returned green.
  - **The catalog grew 95 → 130 types (~330 members)**: the GPU descriptor/fragment/animation entry structs, the
    Fx one-mesh + vertex records, the ground-material chain, the atlas entry/element structs, the database matrices,
    the audio GUID and event-handle boxes, the simulation battle types. `typeprobe` gained **`--exact <name>…`** (which
    types declare this member, one pass over the DLLs) — the tool that made attribution evidence-based instead of
    name-guessing.
  - **bindcheck adjudicated, and caught one of mine**: `WriteContent` attributed to `ContentLayer.vertexBuffer`, whose
    *declared* type is the abstract base — the member lives on the generic subclass, and `Pos` on the Bones-format
    vertex struct the resize path is guarded to touch. Both re-bound to their real types. Final: **`130/130 types |
    0 member(s) missing`**, and `catalog surface: OK — all 275 by-name literal(s) catalogued or allowlisted`.

- **SMOKE TEST, LOAD TIER — automatic at the end of the loading screen (2026-08-21).** User: "move part of these
  tests to the end of the loading screen — as long as the game is not loaded yet, I'm fine with performing tests."
  The seam is `Amplitude.Mercury.LoadingScreen.VisibilityChanged` (a static `Action<bool>` the game's own cursor,
  windows and analytics managers subscribe to; found with the new `typeprobe --find <substring>` mode, no launch);
  `false` = the screen just hid. HAF subscribes at Awake (no Harmony patch, catalogued so bindcheck guards it) and runs
  the **load tier** on the next Update: bindings, registry, roles, assets, sounds, files, GPU budget, district tiles,
  seams — everything that needs only the loaded world, a few ms once. The **full tier** (the F8 button) adds the
  live-pawn checks, which need pawns and a few hook frames. Verdicts are tagged `[load]` / `[full]` in the log, the
  F8 panel and `haf_smoke_report.txt`; `SmokeOnLoad=false` keeps it button-only. Skipped when no registry session
  is registered (a return to the main menu also hides a loading screen).

- **SMOKE TEST READS THE ENGINE, NOT JUST THE REGISTRY (2026-08-21).** A structural review called the verification
  pyramid inverted — 445 tests over ~3k lines of pure logic, a person's eyes over the ~18k that inject. The proposed
  fix (a fake-object seam under the reflection accessors) was declined: reflection is not funnelled (~1,450 sites
  across `GetMember`/`AccessTools`/`GetType`), and a fake would encode the same assumptions about the game every
  drill-caught bug this week disproved. The user's direction instead — *on demand, no per-frame cost, better smoke
  tests* — became three live checks, each for a bug class a drill caught by eye: (1) **skeleton truth** — every live
  pawn slot carrying one of our descriptor ids must sit on OUR skeleton (the tank-destroyer-shows-donor class);
  (2) **pose-hook liveness** — an entry with live pawns the hook hasn't touched in 5 s is a FAIL ("models just
  stopped animating"); (3) **sub-pawn walk coverage** — the walk is re-audited against a full scene scan at smoke
  time (zeppelins / hovercraft / drones were missed this week by a walk that had self-verified before they spawned).
  The classifier is pure (`GatherLivePawnFacts`, 5 tests); the runtime collects slots with the sweep's own loop.
  `ModelEntry.lastPoseHookAt` stamps the hook; the PASS line shows `N live pawn(s) on our skeletons … [pose hook
  fresh], sub-pawn walk W/S`. First in-game run FAILed on the stealth corvette — a RETEXTURE-ONLY entry with no
  skeleton of its own, which the pose hook never matches by design; both checks now gate on an authored skeleton,
  the same lesson the asset check learned on its first run. Drilled: PASS, 19 live pawns across 6 entries, walk 53/45.

- **`disabled` HONOURED ON THE DECLARED-OVERRIDE PATH + the fourth readback site hardened (2026-08-21).** Two review
  findings, both verified. (1) The `e.disabled` check sat on the no-prior-owner branch of the model merge only, so a
  pack whose *declared override* was `disabled: true` still replaced the owner — the debug switch died in exactly
  the case a modder uses it (testing an override against the original). The merge is now the pure `MergeModels`
  (`MergeModelsTests`, 4 cases): a disabled entry never enters the build on any path, and a disabled declared
  override leaves the owner in place with a `DISABLED:` note in the load report. (2) `CropAtlasTile` (the
  `haf_ground_colors.json` readback) restored `RenderTexture.active` and released its temp RT on the success line
  only — a throw in Blit / ReadPixels / Apply would leave the active target pointing at our RT (corrupting the next
  draw) and leak the RT + `Texture2D`. Now `try/finally`, like the three sites the 08-21 sweep had already hardened.

- **SMOKE INJECTION ERRORS: A PER-SESSION LEDGER OF NAMED SITES, NOT A FRAME COUNTER (2026-08-21).** Review finding,
  verified: `InjectionErrors` was an `int` bumped in the per-pawn-per-frame pose hook and never reset, so one throwing
  model turned the smoke's second hard-FAIL signal into a five-digit count, and a transient session-1 error FAILed the
  smoke for the rest of the process — including after a clean reload. Now `NoteInjectionError(site)` adds to a
  `HashSet` of sites (`register`, `repoint`, `fragments`, `pose:<model>`), counted once each; `RearmModelRegistration`
  clears it (under the ledger lock) with the one-shot log flag, so a recurring error re-logs once per SESSION and a
  clean reload gets a clean verdict. The verdict names the sites: `FAIL (1 injection error(s) at pose:TankDestroyers)`.
  Judgment call, recorded: the session-2 smoke forgets a session-1 error — the smoke answers "is HAF working *now*";
  the log keeps the history. Test: 500 frames of one throwing model = 1 error, named.

- **SESSION-SCOPED STATE IS DECLARED, NOT REMEMBERED (2026-08-21).** An external structural critique put it exactly:
  findings like "every descId map gets cleared on re-arm" are rules that exist only in a human's head and a comment,
  across 16 partial files sharing ~150 static collections. Now `Patches/SessionState.cs` gives each one a declared
  lifetime — `[SessionScoped]` (the registry clears it on `Reset(Model|District)`), `[SessionScoped(Manual = "site")]`,
  or `[ProcessLived("why")]` — and `Tests/SessionStateTests.cs` reflects over the assembly and fails on any bare static
  collection. `RearmModelRegistration` / `ResetDistrictSessionState` call the registry instead of ~30 hand `.Clear()`s.
  The first run of the rule found **9 fields the grep-based sweep had missed** and, in triage, **two real leaks**: the
  descId-keyed `sizeFormApplied` / `sizeFormUnitName` had never been cleared (a new session reuses descriptor ids, so
  the formation-by-size swap could silently skip), plus the turn / hug / aim state lists and `freezeLogSkels`,
  `sweepLast`, `pawnLiveLast` (Time.time-stamped; the clock resets per session). Triage: 125 attributed fields.
- **CONFIG `[Debug]` SECTION (2026-08-21).** The `.cfg` a stranger opens had 43 keys on one surface, several labelled
  TEMP / CATERPILLAR investigation / superseded. `DumpPawnRig`, `DistrictDebug`, `DistrictAffinityOverride`,
  `DistrictEvolverGuid` and `AssetNameFilter` moved to a `[Debug]` section (a moved key returns at its default in an
  existing `.cfg` — deliberate, these should be off in play); `TargetMod` and `StateProbePose0Move` — bound, never
  read — deleted. `WonderNativeRows` stayed in `[District]`: "SPIKE" in its label, but it is the live wonder cell fill.

- **PACK TUNING TABLES NOW FOLLOW PACK RESOLUTION (2026-08-21).** An external review agent found — and a read of
  `LoadRegistry` confirmed — that `unitScales`, `eraGrid` and `formationThresholds` were regex-scraped in three loops
  over the raw *discovery* file list, while the models merged over the *resolved* pack list. So a pack that resolution
  had skipped (duplicate `modId`, unmet `dependsOn`) still resized every matching unit; "later packs win" meant later by
  filename, not the player's mod order; and two packs' `unitScales` on one unit composed silently (×0.6 × ×0.6) under a
  framework whose rule is "no silent overrides". Fix: the three loops became one pure `PackTuning.Parse` over the
  resolved `(modId, text)` pairs — the `Pack` now keeps its raw text — that also emits a NOTE per cross-pack interaction
  (shared `unitScales` match with the composed factor; an `eraGrid` row or the `formationThresholds` table taken over by
  a later pack). Notes reach `haf_load_report.txt` as `TUNING:` lines and the log as `[Resize] cross-pack:` warnings.
  Policy in Multi-Mod.md §How packs merge (9); 6 tests in `PackTuningTests` incl. the alphabetical-vs-mod-order case.

- **PER-FRAME COST: MEASURED, THEN CUT 5.7 ms → ~0.6-0.8 ms (16.7% → ~2% of a 30 fps frame) (2026-08-21).** The user asked
  what the day's changes had cost; the answer was an estimate ("under 1%"). `Patches/FrameCost.cs` replaced the
  estimate with a number: every per-frame entry point — the `Plugin.Update` fan-out, bucket by bucket, and the
  per-pawn pose hook (vanilla vs OUR pawns) — timed with `Stopwatch`, averaged over 5 s, shown in the F8 panel and
  logged once a minute as `[FrameCost]`. **The first run read 5,662 µs/frame — 15× the estimate** — and the buckets
  named the causes, none of them the day's changes. Six drilled rounds, each aimed at a measured bucket:
  (1) **two independent full-scene `FindObjectsOfType` scans** (engine audio every 2 s, sub-pawn repair every 3 s;
  ~60-100 ms stalls, ~1.7 ms/frame averaged) → ONE shared source, `UniversalInject.SubPawnScan.cs`: a targeted walk
  of the presentation tree (armies, squadrons, **air formations** — where a squadron's pawns actually live, found with
  the new headless `tools/typeprobe` — battle units) matched by the scan's own criterion and **self-verified** against
  the scene scan once per session (the log names any miss, and the scan stays in charge); (2) the scoped-district
  bind retrying its full leaf walk EVERY frame until the donor layer existed — 42 ms/frame for the first 5 s of every
  load → twice a second; (3) `WonderRows` running an uncached `AccessTools.TypeByName` assembly walk every 30 frames
  forever (1.35 ms/frame) → resolved once + a done-latch, and a scoped wonder latches immediately; (4) the **pose hook
  at 25-57 µs per OUR pawn**: ~60 reflection get/sets on the boxed `PawnEntry` per pawn per frame → `FastMember.cs`
  (compiled `DynamicMethod` field/property accessors with nested struct paths, null = "use reflection") behind
  `PawnFast` (ids, translation, rotation, scale, the nine poses, the four aim slots; each with its own fallback, core
  set gated by `Ready`). The first deploy stayed on reflection because `HideFactor` is a packed PROPERTY — caught by
  the log line, confirmed by `typeprobe`, fixed with property-leaf support; (5) the donor-clip branch (helicopters):
  **two `Physics.RaycastAll` per pawn per frame** for the cliff pre-climb → sampled every 15 frames and held (eased
  anyway); rotor-trim re-resolving every bone name by reflection per line per pawn per frame → resolved once per dial
  edit; the terrain-hug district map's 3-second scene scan → dirty-driven from the district hook (30 s safety net);
  (6) the rest: unit→entry cached per `PresentationUnit` (anim sampler), adaptive respawn-poll cadence, texture tick
  at 1/5, district name cache, flatten poll at 1/10. **Measured at each step, not inferred**: our pawns 57 → ~5 µs.
  **Final drill:** `[SubPawnScan] walk verified against the scene scan: 52 sub-pawn(s), none missed — walk in charge`,
  `[PawnFast] compiled accessors ready`, `[FrameCost] HAF 780 µs/frame (2.3% @ 30 fps)` (565 in a quiet window),
  helicopters hugging terrain, drones / abominations / biremes / the Jagdpanzer all rendering, smoke PASS, 0 errors.
  Unchanged and documented: `SelectorTile` ~0.2 ms (diffuse, 0.6%), and `DistrictDebug=true` in the config costs
  ~40 ms/frame for the first 5 s of every load (the repository dump) — a debug setting, off for play.
  The 30 fps cap matters for reading the numbers: µs/frame is absolute, the percentage is against a 33 ms frame, and
  frame-count throttles run half as often as they would at 60 fps.

- **THE DAY'S ONLY REGRESSION WAS WEEKS OLD: TankDestroyers rendered as its donor (2026-08-21).** Noticed by the user
  mid-drill. Cause: the pack's `pawnDescription` read `Era6_Common_TankDestroyers_01_DRILL` — a drill leftover
  committed to the ENCReload source (d02b00f) — and the runtime matches `addon.IndexOf(pawnDescription)`, so the
  real addon never matched and the smoke said *"no unit on the map this session"* every time, while the unit was on
  screen as a vanilla MediumTank. Fixed at the source (ENCReload 0a8787b) and in the deployed pack; the Jagdpanzer is
  back. So the class can't hide again: the **validator** warns on a `pawnDescription` that doesn't end in `_NN`
  (every game pawn definition does — checkable with no game), and the **smoke**, given the unit-definition names the
  addon hook saw, now says *"matches NOTHING the game loaded — it loaded 'X', which yours only extends (stray suffix
  '_DRILL'?)"* and **FAILS** — the harness had that list all along. Suite 410 → 425.
  Open question recorded, not fixed: the walk showed the hovercraft's and drones' UNIT-definition names do not
  contain their pawnDescription, so `FindEntryForUnitDefinition` (fire-on-attack, engine audio, the anim sampler)
  may be skipping those units too — the next drill's question.

- **THE NINE CLIP ROLES BECOME ONE TABLE — god-class cut A, reversing one slice of a recorded decision
  (2026-08-21).** `ModelEntry` carried each animation role as its own hand-expanded field family — `mca/mcb/mcc/mcd`
  + `moveClipColl` + `moveAnimId` + `moveDur`, ×9 ≈ 63 fields — and every "all roles" site was a hand-written list
  of nine that had to be edited in lockstep: the collection load, the id resolve, the session re-arm, the preflight,
  the smoke test's dead-role check, `AnyStateRole`. Two shipped bugs came from exactly that shape (`AnyStateRole`
  gating on `moveAnimId` alone — critical-review #8; the smoke wiring's dropped `alc` component — why a 36-int
  reflection test existed). Now **`ClipRoles.cs`**: a `ClipRole` enum, a `ClipBinding` (guid, collection, animId,
  duration) per role, `ModelEntry.Roles[9]`, and every one of those six sites is a **loop** — a tenth role is one enum
  value plus its name/tag/key. The named accessors (`e.attackAnimId`, `e.idleDur` …) stay as properties *into* the
  table, so ~200 per-role call sites and the per-frame pose hook are byte-for-byte unchanged. **The pack contract is
  untouched:** the discovery on the way in was that the 36 ints were never the contract — the JSON `clip*` arrays
  are, and both parse paths keep their literal keys (the cross-repo parity gate greps them; it still passes). Gone:
  36 guid ints, 27 runtime fields, `ResolveAnimId` (folded into the loop), one regex-fallback initializer of nine
  40-column lines. Tests: the 36-int reflection theory became a table-driven one (every component of every role arms
  its own check, under its table name) plus `ClipRolesTests` — order, distinct names/tags/keys, fresh table per entry,
  accessors-are-the-table, `AnyStateRole` true for each state role alone and false for primary alone, and **each
  `clip*` key landing on its role on BOTH parse paths** — the test the reflection guard was standing in for. Suite
  390 → 410. [Decisions](docs/Decisions.md) records the reversal and its evidence; the rule now reads: reopen a
  `ModelEntry` slice only with a proven bug from the shape it removes.
  **Drilled:** the howitzer's full state chain (idle-override → pre-move → move → after → recoil on bombard) and a
  creature's idle cue played; every role tag still injects under its own name (`<primary>` 14, `:move` 11, `:attack`
  5, `:idle` 5, `:after` 2, `:premove` 2, `:combat` 1); smoke `verified 35 clip role(s)` — the same count as before
  the table — 0 dead roles, 0 errors.

- **THE DISTRICT AXIS IS ITS OWN CLASS — `DistrictInject`, out of the `UniversalInject` god class (2026-08-21).**
  The review's architecture finding, applied where it pays: `UniversalInject` was one static partial class across 14
  files and ~12,100 lines in which every partial could read and write every other partial's statics — exactly how the
  district session reset came to `Clear()` thirteen collections from a `Sandbox.Load` hook in another file. The
  largest partial (`Districts.cs`, 2,220 lines) and its scoped-visual half (`RepoDump.cs`, 1,647) are now
  **`DistrictInject.cs` + `DistrictInject.Scoped.cs`**, a separate class with an `internal` surface of ~57 members
  (hook entry points, the reset, the smoke test's three reads) and ~300 statics nothing outside can touch. Measured
  before cutting: the district code's only dependencies on the rest of the plugin were the reflection seam
  (`GetMember`/`SetMember`/`BF`), asset loading (`LoadAmpliAsset`/`ParseGuid4`) and `AdjustSkin` — five members made
  `internal` and imported via `using static UniversalInject`; nothing else crossed. What was in `Districts.cs` but
  *isn't* district — pawn props, projectiles, the GPU mesh budget, the atlas dump (355 lines) — stays in
  `UniversalInject` as `UniversalInject.PropsBudget.cs`. Compiled clean on the first pass, 390/390, no behaviour
  change by construction. Also removed on the way: `RearmModelRegistration` called `ResetDistrictSessionState`
  **twice** per re-arm (the log's paired reset lines) — once now, at the canonical call. The standing decision against
  a big-bang `ModelEntry` split ([Decisions](docs/Decisions.md)) stands; this is the bounded cut with a proven bug
  behind it. `UniversalInject` is still ~8,500 lines — smaller, not small.
  **Drilled:** Oracle save loaded first, then the reactor save in the same process — both scoped districts bound and
  textured in their sessions (`DistrictMain … bound 1 building element(s) across 1 tile(s)` for each), the reset
  logged as a **single** line per seam where the morning log showed pairs, always before the first district hook,
  `freed 1 runtime clone(s) from the previous session`, 0 errors, smoke `[1 tile(s) live, 1 scoped, 1/1 textured]`.

- **`docs/Architecture.md` — the invariants, in one place (2026-08-21).** The critical review scored
  *maintainability by anyone else* lowest of every axis, and the reason was specific: the rules the runtime depends on
  — which state is main-thread-only, the publish-once/snapshot discipline on `entries`, the three per-session re-arm
  seams and their order, `texOwned`, the two district ledgers, derived-not-guessed reflection — all existed, as
  comments scattered across six files, findable only by breaking something. Collected into one page: nine sections,
  each rule paired with the failure it was learned from (so it is falsifiable, not folklore) and a test for what
  belongs there (*hard to trace back, and nothing automated catches it*). Indexed under Internals in
  [docs/README.md](docs/README.md) and in `llms.txt`. Transcription, not engineering — which is why it was the cheapest
  point left on the board.

- **THE BINDING CATALOG IS CLOSED — 63 → 91 game types, and the headless checker learns to read its own derived
  entries (2026-08-21).** The review measured the reflection-drift net at ~76 of 88 bound member names and called it
  the cheapest robustness gain left. By receiver it was worse: ~20 game types HAF reaches *structurally* —
  `FragmentEntry`, `SkinnedMeshInfo`, `FxMeshContent`, the district level-build channel chain,
  `PresentationUnitDefinition` and the formation dummy structs, `ArmyInfo`, the Sandbox→empire→science era chain —
  had **no accessor at all**, so a rename in any of them broke a feature with nothing in `haf_bindings_report.txt`.
  A6 adds 28 accessors (all but three **derived** along the exact path the code walks — the A5 rule, no name
  guesses), 28 Deps, and members on 14 existing ones: every Harmony hook *target* (`InitializeCommon`,
  `StartPairMeleeAttack`, `TriggerDeath`, `StartEvent`, `DoStart`, `InstantiatePawn/s`), the battle hold-fire reads,
  the formation builder's fields, the skeleton/fragment/vertex-buffer surface the scaled-clone and hand-prop paths
  rewrite, the scoped-district internals, the Resize Lab's era anchor, and — found last, by a receiver-aware sweep —
  `PawnDefinitionId` off the addon (the descriptor seed that arms the wrong-skeleton net, six sites) and the
  sub-pawn's cached `pawnEntry` (the ghost-rotor source fix). Now **91 types, ~250 members**; what stays outside is
  listed in the catalog itself (the `DistrictDebug`-gated RepoDump dumps, Prober's database lookup, two reads that
  live on runtime subclasses the declared type can't see).
  > **CORRECTION (2026-08-21, same day):** this entry also claimed "every non-diagnostic by-name site". **It was
  > wrong** — a review extracted every member-name literal at a reflection call site and found **~80 not in the
  > catalog**, several on functional paths behind silent catches (`FacingAngleOffset`, `IdleAudioEvent`,
  > `CurrentTechnologicalEraIndex`, `BonesCount`). A6 was a *hand* sweep, so the claim was true of nobody's code for
  > long. Closed properly by A7 below, which adds the members **and the gate that proves the claim**.
  **The finding on the way in:** `tools/bindcheck` — the headless "validates the ENTIRE catalog" tool from 08-16 —
  had never understood `CachedDerived`. It fell back to a bare-name lookup, which happened to work for 5 of the 12
  A5 struct types and reported the other 7 as `[MISSING TYPE]` on a clean build; nobody had run it since the struct
  batch landed. It now evaluates `ElementType` / `FieldOrPropType` / `MethodParamType` chains over the metadata-only
  types, and it earned its keep immediately: it caught **five mis-attributions of mine** before any launch —
  `importAngles` on `FxEvolverMaterial` (it's on `FxMesh`), `allMeshNames` (exists on no assembly — a null-tolerant
  dead read), and three members that live on **runtime subclasses** the declared field type can't see (`WriteContent`,
  `AddNullAtlasInfo`, the descriptor's `materialDataHasChanged` — re-homed on the concrete
  `FxEvolverDescriptorLevelBuildElement`). Headless verdict: `91/91 types | 0 member(s) missing`. Suite 390/390.
  **Drilled live the same day:** `haf_bindings_report.txt` → `game=1.30 verified=1.30 resolved=91/91 type(s)
  missing_types=0 missing_members=0`, log `[GameBinding] OK — 91 game type(s) + their members all resolved` — so all
  37 derived chains also resolve at `Awake`, the one thing the headless check cannot prove. Smoke PASS, bindings ok.

- **THE SMOKE TEST JUDGES WHAT A DISTRICT *SHOWS*, NOT JUST THAT IT BOUND — and it counts both render paths
  (2026-08-21).** Two defects in the harness's own honesty, found by reading the log next to the F8 line. (1) The
  district "tiles live" count read only the ISOLATE ledger (`DistrictModel.tiles`); the SCOPED path (the reactor,
  data-authored selector) keeps its tiles in `ScopedState.refreshPlbcs`, so the smoke printed *"districts authored
  but 0 tiles live — district path UNTESTED"* in the same session the log said `bound 1 building element(s) across
  1 tile(s)` — the vacuous-coverage note was itself dishonest. The caller now hands the pure `GatherDistrictFacts`
  whichever ledger owns the district (`TryGetValue`, never `ScopedFor` — the smoke must not *create* state), and the
  line reads `[1 tile(s) live, 1 scoped]`. (2) A live tile proves the **mesh** bound, nothing about whether OUR
  albedo landed on it — and both apply paths, after 3 exceptions, **give up by latching `texApplied=true`** so the
  poll stops, which makes `texApplied` alone read as success on a district rendering untextured. New
  `DistrictTexState` (lifted off either ledger) is judged **`texErrors` first**: gave-up → **FAIL**, named, pointing
  at the `[DistrictTex]`/`[DistrictTile]` log tag; applied → `1/1 textured`; pending → a NOTE (≥300 polls: "asset not
  resolved"; fresh: "re-run in a few seconds"); no atlas or no live tile → not judged. Seven tests, including the
  latch trap (`Applied=true, Errors=3` must FAIL) and the off-screen case (a stale error count on 0 live tiles must
  not). **Drilled:** reactor on screen, line 84606 `2 district(s) [1 tile(s) live, 1 scoped, 1/1 textured]`, 0
  errors, 0 give-ups. Suite 383 → 390.

- **TWO DATA RACES ON "THREAD-SAFE" PATHS — the reflection cache and the district reset (2026-08-21).** A critical
  review asked a question this codebase had never asked of itself: not *"does this touch the Unity API off the main
  thread?"* (guarded everywhere, correctly) but *"does this touch shared heap off the main thread?"* Two holes, one
  blind spot. (1) `memberCache`/`fieldCache` in `UniversalInject.Reflection.cs` were plain `Dictionary`s annotated
  *"Main-thread only"* — yet the sim-thread hooks read through `GetMember` too (`FireProbe.Member` on
  `ArtilleryStrikeStarted`, `OnBattleStarted`'s contender walk, `FacingPersist.OnSave/OnLoad`), and the members they
  touch (`StrikerUnit`, `AttackerGroup`, `Contenders`, `StorageContainerInfo.Name`) are touched by **no** main-thread
  path — so their first use is a guaranteed *insert* racing the per-pawn-per-frame reads. A `Dictionary` resized under
  a concurrent reader corrupts its bucket chain: `FindEntry` spins forever — a hard freeze, no exception, no log line,
  invisible to every one of HAF's loud-failure mechanisms. Now `ConcurrentDictionary` + `GetOrAdd` with static
  factories (same null-caching, lock-free reads, no closure per miss). (2) `RequestSaveLoadRearm` ran
  `ResetDistrictSessionState` **inline on the `Sandbox.Load` hook's thread**, justified in three separate comments as
  *"pure reference-nulling (thread-safe)"*. It `Clear()`s ~13 collections the main thread's per-frame polls read and
  write (`trackedDistricts`, `loadedSelectorByKey`, `scopedStates`, the wonder-template caches, every district's
  tiles/leaves/boundSlots) — one corruption window per save-load. It could not simply move to `Update`: the reset must
  beat the district presentation hooks during the rebuild or they bind onto corpse leaves (the Oracle incident). So
  `Sandbox.Load` now only **flags** it (`volatile districtResetPending`) and `ConsumePendingDistrictReset` runs it on
  the main thread from the top of `ConsumePendingReloadRearm` **and the entry of every district Harmony handler** —
  the first district to build in the new world performs the reset itself, before binding. Idempotent; one volatile
  read per repeat. **Drilled:** two sessions, the reset logged before every district hook line both times, the
  reactor bound across 1 tile, artillery fire + the off-thread `StorageContainerInfo` lookup exercised, 0 errors. The
  07-19 adversarial rounds had signed off "cross-thread sample locking" as clean — and it was; the lists were
  locked. The caches and the reset were never on that list. Rule recorded in [Decisions](docs/Decisions.md).

- **THE STALE `baker/` EDITOR SNAPSHOT IS GONE — the last cross-repo copy (2026-08-21).** 13 files, **~7,000
  lines**, mirrored from ENCReload and carried since 08-01 as a "deliberately stale reference snapshot" with a
  warning header instead of a fix. Deleted. Its own README admitted the copy's `ModelDef` was missing fields the
  plugin reads (`scale`, `animPhaseSpread`) plus bake-time ones (`staticParts`, `localNodeAnim`, `bakeLocked`,
  `deployStripExtra`), so a `pack.json` written from it **silently omits** them — the affected models render at
  default scale and default phase spread with no error anywhere. That is a documented hazard, not a guarded one,
  and this project had already paid for the same disease once: the glbconv split-brain that shipped the T5
  mirrored-winding fix regressed (08-17).
  **Why now, when 08-01 decided otherwise.** That decision was against a *blanket* delete of `baker/`, which was
  genuinely unsafe then — the folder also held the live `glbconv/` and a `Tools/` Blender-script copy. Those
  Blender copies went on 08-17, so a **targeted** delete of just the editor `.cs`, leaving `glbconv/` and
  `reactor_silhouette.py` untouched, carries none of the original risk. And the calculus changes at release: today
  the snapshot is a trap a maintainer knows about; after ENCReload 2 ships it is plausible-looking editor source
  that an adopter finds, drops into Unity, bakes from, and produces quietly broken packs with.
  Nothing lost — git history keeps the bytes and ENCReload holds the authoritative copies. The csproj exclusions
  stay (they keep glbconv's `Program.cs` and its .NET 8 publish output out of the plugin build); build clean,
  383/383. The [Decisions](docs/Decisions.md) rule is now unqualified: **a cross-repo copy is either authoritative
  or it does not exist** — if one seems necessary, the honest options are a submodule or a link, never a snapshot
  with a warning on it.

- **THE PER-FRAME POSE DECISIONS BECOME TESTABLE — and the oracle catches what reading could not (2026-08-21).**
  `StatePose` / `DeployPoseTime` / `FireOncePoseTime` / `RecoilOverlay` decide, every frame for every pawn, WHICH
  clip plays and WHERE in it — the thing the player actually sees — and did it tangled with `GetMember` reflection,
  `Time.time` and the locks around the shared sample lists, so none of it could be tested. Second application of the
  extraction rule ([Decisions](docs/Decisions.md)): the decisions now live in the pure `Patches/PoseMath.cs` —
  the proximity-weighted state vote, the attack/after/pre-move windows, the nearest-fire match, the deploy ramp and
  the recoil sweep — while the callers keep the I/O **and the locks** (the lists are still shared). **-94/+29 lines**;
  suite **329 → 383**.
  **The one behaviour change, found by the parity oracle rather than by reading.** The two nearest-fire call sites
  looked like the same loop written twice. They are not: the recoil overlay seeded `best` with the radius
  (`d < r²`, strictly inside), fire-once seeded with `float.MaxValue` and range-checked afterwards (`d <= r²`,
  inclusive) — so they disagreed for a fire at **exactly 4.0 units**, where fire-once counted the pawn as the firer
  and recoil did not. Unified to strictly-inside, matching what the other two matchers already do; the inclusive
  form was an artifact of the spelling, not a decision. Pinned by a test that spells out both old behaviours.
  **What the mutation drill taught, and it is worth more than the tests.** Six mutations: four caught loudly, one a
  genuine equivalence (a sample exactly on the radius carries weight `R²-d² = 0`, so `>=` vs `>` cannot matter), and
  one — replacing the proximity weight with a headcount — sailed past thousands of generated layouts. That is not a
  corpus-tuning problem: the two rules only disagree on small unbalanced in-range splits, and as the sample count
  rises the majorities converge, so a **bigger corpus fires less often**. Widening the draw and enlarging the
  formations both failed; only an adversarial hand-written case catches it. **A generated corpus pins that code was
  COPIED faithfully; hand-written adversarial cases pin that it DECIDES the right thing — and a mutation drill is
  how you learn which one you are missing.**
  **DRILLED in-game the same night.** A full session across seven injected types (howitzers, organ gun, drones,
  mech, tanks, helicopter, abominations) with live varying pose times and **zero HAF frames in any stack trace**;
  then the closing case, the path carrying the boundary change: a **towed howitzer bombard** — `[Fire] *** OUR MODEL
  'TowedGunHowitzers' FIRED`, `armed 1 pawn(s)` (the firer, not the battery), and that gun alone sweeping
  `t=0,941 → 0,949` up the recoil tail while every other unit on screen held `t=0,585` in the same frame. Recoil
  confirmed by eye. Not extracted: the idle-alt cadence — it draws from `Random` and mutates scheduling state, so it
  is a scheduler rather than a decision, and needs an injected clock + RNG.

- **THE DIALS STOP SWALLOWING TYPOS — and the drill catches what 323 green tests missed (2026-08-20).** All four
  live `haf_*.txt` dials (rotor trim, turn ease, terrain hug, battle turn) inlined their own `key=value` loop
  inside a `Poll*` method, wedged between `File.ReadAllText`, the Unity clock and live-pawn reflection — untestable,
  and all four shared one behaviour: **any line the parser did not recognise was `continue`d away in silence.**
  `radus=6`, `hoverbanks=12`, a European `rate=1,5` each produced a working plugin that quietly ignored the
  setting, with nothing in the log — the "silently disarmed" class, sitting in the one part of HAF a user
  hand-edits mid-session. The parse is now the pure `Patches/DialConfig.cs` (text in, typed config **plus a list of
  problems** out; **-85 lines** from the four methods), the `Poll*` methods log what comes back, and a typo names
  its own line number and the valid keys. Suite **120 → 329**.
  **Guarded two ways, because extracting shipped code can change it:** a *legacy parity oracle* keeps the original
  inline loops verbatim in the tests and compares values over a 39-case corpus × 4 dials — which immediately found
  a latent bug (`@1=5`, a bone-less line, produced a trim with an empty bone name, and since `IndexOf("")` is `0`
  for every string it silently rotated the **first bone of the skeleton**; now dropped and named) — and a *mutation
  drill*, where 5 of 6 planted mutations each failed the suite loudly, the sixth correctly passing as a genuinely
  equivalent implementation rather than a gap.
  **Then the in-game drill earned its place.** Six provably value-neutral faults planted in the live dials:
  all six named with correct line numbers, values byte-identical to the pre-change run, `reloaded 0 line(s)` for
  the `@1=5`, and — the negative control — **zero warnings once the faults were removed**. And it found a bug the
  whole green suite had missed: the `[Hug]`/`[TurnEase]` echo lines used plain interpolation, so on a
  comma-decimal machine the log printed `lookahead=1,5` — *the exact spelling the parser rejects*, one line above
  the new warning saying "use '.' for the decimal point". Copy it back into the file and it silently dies. Fixed
  (`DialConfig.Inv`) and pinned by a round-trip property — whatever the log prints must parse back — asserted
  under `nl-NL`, and drilled (reverting the fix fails 3 tests). The lesson is the old one at a new scale: the unit
  tests are the code's opinion of itself, and both halves shared the same blind spot. Pattern + rules:
  [Decisions](docs/Decisions.md), [Testing](docs/Testing.md).

- **DISTRICTS + FORMATIONS INHERIT THE COLLAPSE (2026-08-20).** Units got the ONE-file registry on 08-19; the
  district and formation registries still ran the old two-file pattern with none of its protection. Rather than
  two more hand-copies, the machinery moved into a shared `SingleSourceRegistry<TFile>` engine — git-tracked
  source, deployed build artifact regenerated on every Save, one-time migration, artifact recreation + drift
  warning, pinpointed corruption (line/column), timestamped preservation, once-only logging, Save lock, and
  one-click recovery from the last deploy or the last commit — and `DistrictRegistry` / `FormationRegistry`
  became thin typed shells (188→137, 181→133 lines; public API unchanged). The District Factory and Formation
  Override windows carry the Factory's red recovery banner. Two rules the shared engine adds over the first
  cut: migration **never overwrites a newer source with an older deploy** (the loser is preserved beside the
  artifact), and content comparisons are **CRLF-normalized** — found necessary on the spot: the live district
  source and deploy differed by exactly 143 carriage returns and nothing else, which would have fired a false
  "hand-edited" warning on the first load. Source files keep their historical `.backup.json` names to spare
  git a rename; `SourcePath` is the honest accessor. Backlog #3 follow-up closed. **DRILLED the same evening**: a
  comma deleted from the district source → banner pinpointing *line 22, position 16* (`districts[0].rotation.y`),
  one Console error, the corrupt copy preserved timestamped → **Restore last deploy** recovered 2 entries eleven
  seconds after the break. Drill 3 (deployed `haf_formations.json` deleted, Refresh pressed): the artifact was
  recreated at the exact second of the click — but the user saw nothing and called it "proof that it does not
  work", which is its own finding: a self-healing event that only speaks in the Console is invisible to the
  person who triggered it. The engine now records a notice (artifact recreated / source adopted) that the window
  shows in its status line on Refresh.

- **PROBE PARTS 116 s → 7 s (2026-08-20, "can you optimize it?").** The Ehrhardt probe had crept from half a
  minute to two. Per-phase timers (now permanent `VEHICLE timing:` lines) convicted two phases, neither the
  split nor the import: the escape-ray **visibility** pass (31 s — `scene.ray_cast` walked all 3,350 objects per
  ray) and the **preview export** (86 s — the FBX exporter writing 3,350 *skinned* objects, 58 MB). Fixes: one
  `BVHTree.FromPolygons` over the world-space scene (0.3 s, same verdicts ±3 parts at the eps edge) and an
  unskinned meshes-only preview (2.1 s, 11 MB). Because the bone-row highlight had just been built on the
  preview's skin weights, the probe now emits each shard's dominant bone as a 7th `PART` field and the Lab
  maps bone → shards from the part list — which also made visible what the user had been hunting: the Turret
  bone owns **567 shards**. Plus a sentinel + flush after the PART lines, because Blender's late-flushed
  version banner glued itself onto the last row during verification.

- **VEHICLE-LAB CLOSERS (2026-08-20).** The two loose ends from the canoe forensics, closed in one pass.
  (1) *The recipe-predates honesty note*: loading a recipe now names the features it predates ("recipe
  predates: wave rock, spin switch — loaded as safe defaults; Save to modernize"), detected by key-presence
  in the raw JSON since JsonUtility can't tell absent from default — the invisible fallback that cost GLB
  forensics to diagnose now announces itself (7 of the 9 recipes on disk trigger it today). (2) *The
  hand-list gate grew a third block*: every `Recipe` DTO field must be written by SaveRecipe AND restored by
  LoadRecipeFromPath, or the push fails naming the field — drilled by planting a field, caught on both sides
  (23 fields round-trip green). The canoe-style silent field loss is now structurally impossible, the same
  treatment the Factory/Lab ownership lists got on 08-19. Same evening: the **Edit existing** dropdown shows
  each recipe's last-modified stamp — a bare name list can't tell you which one you worked on yesterday. And a
  drill-by-use catch: "the Ehrhardt stopped highlighting" — it never had: a rigged SKM source defaults to the
  fast path whose rows are BONES, and the highlighter matched renderers by name. Bone rows now tint every shard
  whose skin weights point at that bone (tallied once per preview); shard rows match exact-before-prefix.

- **THE BAKE TESTS WINDOW (2026-08-20, "this looks ridiculous").** The test pyramid had grown one guard at a
  time into seven bare menu items — "Bake Conversion Gate Test (litmus)"? — each talking to its own dialog,
  with no way to tell what a test did without reading source. The user called it: *"we need a specialized
  testing dialog with clear explanation what we are testing… the center testing suite with clear UI
  feedback."* All seven items collapsed into **one window** (`Tools ▸ HAF ▸ Bake Tests…`): every test is a row
  with a plain-language what-it-tests and what-it-costs, Quick/Everything presets, one Run button, LIVE
  per-row PASS/FAIL (a delayCall queue runs one test per editor tick so rows turn green/red as they finish;
  failures unfold their detail lines), and a durable `Logs/haf_bake_tests_report.txt` per run — the editor
  twin of the runtime's `haf_smoke_report.txt`. The tests themselves (`BakeSmokeTest` / `BakeFeatureTest` /
  `ConversionGateTest`) now return a `BakeTestSection` instead of popping dialogs — and SKIPs are counted
  honestly instead of being smuggled into the pass count. Same day, the suite's first ALL-models run earned
  its keep in reverse: 6 "failures" that were actually a **stale assertion** — the smoke test's 1 KB
  pose-stream floor predated the one-frame `Spin[0..0]` idle pattern, whose real shipped pose streams run
  48–960 bytes. The test would have failed the live, in-game-working assets; the boundary (every failure a
  one-frame idle, every pass a multi-frame clip) convicted the test, not the bakes. Floor recalibrated to 32
  bytes (smallest legitimate asset: 48) with the conviction recorded in a comment. First-sight drill catch by
  the user: the two smoke scopes could both be checked (baking every model twice) — they're now mutually
  exclusive radio rows, and the Everything preset picks the thorough one. The user then drove three more UX
  rounds: row titles became the plain question each test answers ("Is rig conversion still correct?" — the
  jargon 'litmus'/'smoke'/'golden' demoted to the descriptions), and a two-round layout fight (titles clipped
  to "…(c", then cramped into a ~180px column) unearthed a real Unity gotcha — `ToggleLeft` with a
  word-wrapping style mis-sizes itself; the fix is a checkbox + label-skinned button that takes the full row
  width. **DRILLED 2026-08-20**: the full Everything run — all seven rows, smoke-ALL through deploy goldens —
  came back 54 passed, 0 failed, 1 skipped (the texture-only corvette) in 6.3 min, one report file.

- **THE SIZE-REFERENCE KIT (2026-08-19, user-designed piece by piece).** "A default humankind man as a
  reference would really help assess size" → both previews (Factory + Lab) gained a **Ref man** — a stylized
  figure at game human height with X/Y position dials — and a **Ruler** (vertical stick, 0.5u ticks, long
  ticks at whole units; units not meters, since every bake picks its own world scale). His height was
  **calibrated 0.9 → 1.1 → 1.85** the waterline way and verified head-to-head against a human-scale soldier
  model. The build was its own three-round drill: hand-rolled winding culled every face ("I don't see any
  man"), shared-vertex double-siding zeroed the normals, and the final form — flat-shaded triangles in both
  windings via one shared box-prop builder — is now the house pattern for preview props. Rendering hand-built
  meshes joins the lesson list: emit both windings with per-face vertices, always. **Art-direction rounds
  (same evening, user-driven)**: classical figure proportions (head ~1/7, legs half with a gap, arms to
  mid-thigh), slimmed depth, and a sphere head — itself converged by bisection (0.072 too big, 0.055 too
  small, 0.063 landed) at 14×20 tessellation so flat shading still reads round. Every dimension is now a
  named parameter; further taste changes are one-liners.

- **VEHICLE LAB: RECIPE FORENSICS + THE SPIN MASTER SWITCH (2026-08-19).** The canoe's recipe "lost" its wave
  configuration — actually never had it: the recipe predates the wave fields, and absent JSON fields load as
  C# defaults (the honest choice; inferring "wave on" from the `_Wave.glb` filename would be guessing). The
  values were **recovered by forensics on the shipped GLB** — decoding the 361-key quaternion track gave pitch
  2.4° × 1 swing, roll ≈ 0, over a **15-frame** cycle — which also caught the restore's own first error: the
  modern default of 120 rock-frames would have slowed the shipped bob 8× (at generation time the rock-frames
  argument didn't exist and fell back to Spin frames = 15; the rig script's clip length is
  `max(spin frames, rock frames)`, the one real spin↔wave coupling, now named in the UI instead of hidden).
  Then two UX rounds on the Spin section: it **grays out as "inert"** when no wheel/rotor/turret is marked
  (with the clip-length exception disclosed), and — the user's follow-up exposing the gating's blind spot —
  an **"Enable spin animation" master switch**: disabling spin on a *wheeled* vehicle used to mean unmarking
  every wheel (the wave-checkbox lesson of 08-01, relearned). Off = zero spin degrees + forced-static tracks
  at Generate; bones, markings and dials all survive toggling; the recipe field defaults TRUE so old recipes
  keep their motion. Recipe save/load hand-lists updated on both sides.

- **THE LOGGING AUDIT — and the two real holes its questions exposed (2026-08-19).** Asked "how good is our
  logging?", the survey said: 707 plugin log calls, 264 editor calls, 10 machine-readable files, 12 one-shot
  guards — runtime logging excellent and battle-proven, with three gaps, all filled: (1) **invariant
  formatting** as infrastructure + policy (`Plugin.Inv`; a wrapper can't retro-fix current-culture
  interpolation, so the combatZ line is the live exemplar; config parses audited culture-safe already); (2)
  **`Plugin.Once(key)`** — a keyed one-shot gate so log-once stops being hand-rolled statics (15 legacy guards
  stay — several are load-bearing state; the dead one deleted, making the build warning-clean); (3) a
  **durable editor action log** — every HAF-prefixed Console line appended timestamped to
  `Logs/haf_editor_actions.log` (5 MB rotation), because Editor.log is per-session and unstamped. Then two
  follow-up questions found real holes: **"are the logs backed up?"** exposed that the backup config group's
  `*.json` glob silently missed every hand-tuned runtime file (hug/turn/battle/rotor tuning, the plugin .cfg,
  ground-tex/state dirs — not regenerable, not in git, in NO backup; now included), and the fix trail exposed
  that the **compile gate's hand-listed sources had drifted** — three editor files (GameSoundLab, HafCli,
  SoundOverrideRegistry) were NEVER compile-checked; sources are now discovered at run time, retiring that
  hand-list for good. Questions are drills too.

- **CORRUPT-SOURCE PINPOINT + ONE-CLICK RECOVERY (2026-08-19, user design: "not only a try/catch but recovery
  functionality") — DRILLED.** A hand-edit that breaks pack.json now gets: a **pinpointed error** (Newtonsoft
  re-parse purely for diagnosis — the drill's planted missing comma reported as "line 19, position 12, path
  models[0].scale"); a **timestamped preserved copy** (a second corruption never overwrites the first's
  evidence); and a **red recovery banner** in the Factory with one-click paths, each validated (must parse and
  hold models) before writing: *Restore last deploy* (the artifact — freshest valid copy, no git needed),
  *Restore last commit* (git checkout, then validated like any candidate), *Open broken file* (fix the named
  line by hand). Save/Bake stay locked until recovered — the no-wipe guarantee unchanged. Drill: planted comma
  → banner named the exact line → Restore-last-deploy brought all 22 models back with the corrupt copy
  preserved. Drill finding fixed same hour: the corrupt error logged on EVERY Load poll (dozens of Console
  lines for one broken file) — now once per corruption; the banner is the persistent surface. Also from this
  exchange, a process lesson recorded: the editor already logs its actions — the drills' narration burden was
  the operator not reading Editor.log.

- **THE PACK.JSON COLLAPSE (2026-08-19) — one registry, one truth (backlog #3 closed).** The deployed/project
  pair — deployed authoritative, project a dual-written shadow — surprised every external tool and fed the
  coherence-drill era. Flipped to the honest model: the **git-tracked project file is THE registry** (the
  editor reads/writes only it), and the deployed copy is a **build artifact** like the DLLs — regenerated
  atomically on every Save, recreated on load after a game reinstall, never read back. Hand-edits to the
  artifact are detected and warned about once per session (the next Save overwrites them); a one-time
  per-machine migration adopts pre-collapse deployed state into the source, and a missing source adopts the
  artifact (a fresh clone against a live install loses nothing). Artifact-refresh failure is loud but never
  fails a Save — the source is safe, the game just runs stale until the next success. Scope: the model
  registry; districts/formations keep the old pattern as follow-up candidates. **DRILLED same evening, all
  five steps**: Save advanced both files in the same second, byte-identical; a planted hand-edit in the
  DEPLOYED file drew the build-artifact Console warning; deleting the deployed file had it recreated on the
  next Refresh **from the edited source** (proving authority — the artifact hand-edit was wiped exactly as
  promised); and a planted external edit of the SOURCE raised the coherence banner, closed by ↻ Reload. The
  drill also caught one label straggler: the Lab's footer still named the deployed path — fixed to name the
  source.

- **OFFSITE BACKUP: VERIFIED END-TO-END — AND SELF-RECOVERING (2026-08-19).** The one backup layer never
  watched succeed finally got its drill: a manual "Back up now" produced a registry-verified snapshot AND its
  count-verified offsite zip (1.06 GB, atomic rename completed). The verify also caught a real gap: the
  morning's daily auto backup's zip had **died mid-write when a recompile killed the background thread**,
  leaving a stale `.partial` and no final zip — silently, with no retry (the atomic design prevented
  corruption but not absence). Fixed: on every editor load, before the daily-auto pass, stale partials are
  deleted and any backup folder missing its final zip is **re-zipped automatically** (count-verified by the
  same core; a reload can never race a live writer because the reload is what killed it). The stale 21:01
  partial doubled as the fix's natural drill — **DRILLED same evening**: the next reload deleted the stale
  partial, re-zipped in the background, and the count-verified final zip (1.06 GB) landed by atomic rename at
  21:10 with zero partials left. Every layer of the backup system has now been watched succeed.

- **THE HAND-LIST GATE (2026-08-19) — the audit's residual risk, closed.** The Factory/Lab ownership-rebase
  lists were guarded only by MAINTENANCE-TRAP comments — a future UI field could still be silently reset on
  Save (the combatZ class). `Tools/check_handlists.sh` now runs the audit's exact mechanics on every push:
  UI-edited fields diffed against each window's re-apply list, any miss failing the push by name with the fix
  pointed at. **Drilled at birth**: planting the historical combatZ omission produced the named FAIL; the
  restore went green; the very push that shipped the gate ran through it. Factory 30/30 covered, Lab 40/40.
  The silent-reset class is now structurally impossible, not merely documented.

- **MULTI-SMR PREVIEW SLICE (2026-08-19) — the known future ambush, closed before it fired.** The preview-
  texture fix persisted ONE atlas-remapped clone while the bake remaps *every* skinned renderer's mesh — a
  single out-param the loop overwrote, so the first multi-renderer rig baked would have replayed the corrupt-
  texture saga on its other parts. Now: the baker persists one clone per renderer (`_PreviewMesh`,
  `_PreviewMesh1`, … — index 0 keeps the historical name so existing bakes stay valid, and the numbered set is
  swept on every re-bake and in the static-over-animated cleanup); **one shared loader** feeds BOTH preview
  windows and all call paths (the pattern-copy lesson applied up front this time — copies grepped, none left);
  each renderer match-and-consumes its clone by vertex count, and the loud log reads `APPLIED n/m` with any
  unmatched clones named. The list-of-one path is exercised by every existing multi-material model; the true
  multi-clone path awaits the first multi-SMR bake.

- **SMOKE TEST, FIVE-POINT UPGRADE (2026-08-19; user call: "can we apply all?").** The F8 harness closed its
  five known gaps in one pass: (1) an **ObjectSpace write-back self-test** — one live pawn entry is mutated
  and re-read through the exact boxed-struct chain every runtime offset uses, so the combatZ died-in-the-box
  class (previously findable only by a battle drill) is now a hard FAIL from one F8 press; (2) the silent
  19-of-22 delta is **named** — uninjected entries are listed with a diagnosis (disabled vs no unit on the
  map); (3) the verdict is written to **`haf_smoke_report.txt`** next to the load and bindings reports — a
  headless/CI launch can now assert all three files clean; (4) **sampler health** — entries whose features
  need the state/combat sampler but hold zero samples are noted (a gate regression is visible without a
  battle); (5) **vacuous-coverage notes** — a green segment that verified nothing says so ("districts authored
  but 0 tiles live — UNTESTED this session"), keeping PASS honest per the silence-is-not-success rule. The
  verdict stays a pure function; 4 new tests (120). **VERIFIED in-game same day**: PASS with `seam write-back ok`, the three uninjected entries named with the benign diagnosis (DugoutCanoe/ReconZeppelin/VolleyGun — no unit on the map), the district UNTESTED note showing, and `haf_smoke_report.txt` written. All five features live.

- **THE STRUCT BATCH (2026-08-19) — derived bindings close the drift net's last silent surface.** The bindings
  census covered 50 named types, but the structs HAF pokes hardest — `PawnEntry` and its `ObjectSpace`/pose/
  bone-rotation slots (the GPU seam written every frame), `Skeleton`/`BoneInfo` (preflight + injection), the
  army/battle walk (the state sampler) — were absent, because the code reaches them STRUCTURALLY (array
  elements, field values) and their names never appear anywhere. The fix follows that fact: each struct is
  **derived from its anchor member** — `PawnEntry` = element type of `PawnManager.pawnEntries`, `ObjectSpace` =
  that struct's field type, and so on — the exact path the runtime walks, so the census has zero name-guessing
  and zero false-positive risk. Nine derived entries (+ widened members on three existing ones; inventory was
  mechanical — every `GetMember`/`SetMember` literal grouped by receiver). A game update that renames an anchor
  reads `[MISSING TYPE]`; a reshuffled struct member reads `[MISSING MEMBER]` — one named line in
  `haf_bindings_report.txt` instead of torn skinning or a silently dead offset (the combatZ write-back's own
  seam is now censused). Host-proven with 3 new tests (115 total): derivation across field/array/generic/
  non-public/property anchors, broken-anchor → null-not-throw, derived types flag members like any Dep.
  **GAME-VERIFIED same day — and the first launch was its own drill:** all nine derived structs resolved
  (`missing_types=0`), while the report flagged **three members I had attributed to the wrong receivers** — the
  A1 lesson relearned live, caught by the report's self-validation exactly as designed (`OutputLayerInstance`
  belongs to the atlas-dump walk's content entries, not `PawnEntry`; `AttackerGroup`/`DefenderGroup` to the
  SIMULATION battle the war-cry hook receives, not the presentation battle). Re-homed via three more derived
  accessors — including a method-parameter derivation for hook types — plus the war-cry chain censused. Final
  verified state on game 1.30: **`resolved=63/63  missing_types=0  missing_members=0`**, F8 Smoke Test PASS
  with `bindings ok` folded into its verdict. 116 tests. From this launch on, a Humankind patch touching any of
  these structs announces itself by name at boot.

- **v0.1.0 — THE FIRST TAGGED RELEASE (2026-08-19; withdrawn to draft the same day).** Both repos tagged
  `v0.1.0`; a GitHub release with an extract-into-game-root zip (plugin + schema DLL under `BepInEx/plugins/`
  + INSTALL.txt) and release notes distilled from this changelog. Everything the preceding weeks built made
  this shippable: CI from public sources, the four-surface pack validator, Ship Status, drilled entry-state
  coherence and backups, the bindings drift net, 112 tests. **Unpublished at the user's request shortly after
  release** — kept as a DRAFT (asset + notes intact, re-publishable in one click); the git tags remain.

- **THE HAND-LIST & LABEL-LIES AUDIT (2026-08-19)** — backlog #4, the last open entry-state coherence item,
  executed mechanically rather than by eyeball: every field the Factory/Lab UI edits was extracted by pattern
  and diffed against every hand-maintained list (the Factory ownership rebase, the Lab ownership rebase, the
  bake-config capture), and every "runtime / no re-bake / applies on load" claim was read against its actual
  code path. The lists came back **complete** (34/56/29 fields, zero uncovered) — the combatZ drill-catch the
  day before had already fixed the one real hole. Three findings, fixed same day: **Make static left
  gunElevMax/gunElevAxis/animPhaseSpread alive** (gun elevation is runtime-applied to every non-donor entry, so
  a made-static gun kept its elevation behavior — precisely the "cursed leftover" class Make static was built
  to kill); the **Save-settings status** claimed Position offset/Size apply on load — false for static entries
  (now says which fields are baked, per entry type); **Browse's auto-set of animUnitFix** is discarded by Save
  settings (animation-owned) — the status now discloses it. One stale specimen retired: the tris slider already
  discloses its double-sided halving in tooltip and bake log. Residual, accepted: the hand-lists are guarded by
  MAINTENANCE-TRAP comments, not by a gate.

- **COMBAT HEIGHT OFFSET — the diving submarine (2026-08-19, user-designed).** "It would be cool that in combat
  they would be actually submerged": new shared field **`combatZ`** (schema field 67; 0 = off) — world units
  added to a unit's height while its army is battle-locked (deployment → resolution), negative dives, positive
  lifts, **eased 2s both ways** via a combat-flip timestamp carried in the state samples. Works for STATIC and
  animated entries alike: statics bake their Position offset into the mesh, but a state-dependent offset can
  only ever be runtime — this is their one legitimate runtime translate, applied at the same proven per-frame
  ObjectSpace seam as everything else. Combat stance comes per-pawn from the battle-lock sampler the
  state-driven clips already read; its gate now admits `combatZ` entries, so a plain static sub joins sampling.
  Authoring: a Flight-character slider plus an **"In combat" preview toggle** — the model drawn at battle-locked
  height with the keel/top readout following, which is how the submarine was calibrated to snorkel-only trim
  (top +0.05u vs the crest-inclusive waterline; `combatZ` −0.13). Validator range rule + test (112 tests);
  parity green at 67 fields/80 parsed keys. **Editor-side drill caught a real bug the same hour:** Save reset
  the new field to 0 — `RebaseLabOwnedOnRegistry` re-applies only a hand-maintained Factory-owned field list,
  and the new field wasn't on it. Fixed, and the list now carries a MAINTENANCE TRAP warning (a new Factory
  field needs: schema, regex fallback, UI, and that list — the parity gate does not check it; same silent-reset
  family as the label lies). **DRILLED same day — and the in-game drill caught the third
  last-line-of-the-pattern omission in two days:** the first battle showed NO dive despite the engaged log,
  because `ApplyCombatZ` copied `ApplyPositionOffset`'s boxed-struct pattern but dropped its final
  `SetMember(entry, "ObjectSpace", os)` write-back — the offset was computed and logged, then died in the box
  (the log proved the COMPUTATION, not the WRITE; the user's flat "I did not see any change!!" was the accurate
  instrument). One line fixed it; second battle verified: snorkel-only above the swell, hull a shadow beneath,
  eased dive and resurface. The pattern now has three drill-caught members (the Lab-port call site, the
  restore-path substitute, this write-back) — when copying a working pattern, its LAST line is the one you
  drop, and only executing the scenario notices. Also same day: preview zoom-in deepened 5× (0.1 → 0.02
  minimum distance factor) for close-up trim inspection.

- **VEHICLE LAB POLISH (2026-08-19, both user finds).** (1) The **Static tracks** isolation switch moved to the
  top of the Spin section and now gates the tread dials (speed/detail gray out when the tracks won't run —
  decision before dials). (2) **Save recipe… kept reverting to the raw model's name**: saved as `prod3`, the
  next save suggested `prod2` again — the dialog default derived from the source file every time, while the
  window already tracked the current recipe name for its combobox. It now defaults to the tracked name,
  falling back to the source-derived name only for a never-saved session.

- **ANIMATION LAB PREVIEW TEXTURE-CORRECT (2026-08-19)** — the user caught the day-old Factory first-select
  texture fix stopping at the Factory's window: the Lab's fit preview has its own copy of the renderer-flattening
  loop and still paired original FBX UVs with the packed atlas on load. The substitution was ported — and the
  port itself was drilled into a second finding: it landed in the rebuild path while the domain-reload *restore*
  path (the one that runs right after a compile) still drew unsubstituted. Both fixed, user-verified. Postmortem
  epilogue added: a fix in copied code needs grepping for the copies, and a fix has as many deployment points as
  its code has call sites.

- **THE WATERLINE, CALIBRATED (2026-08-18) — vessels now preview at the game's true water level.** The submarine
  that "looked right in preview, near-invisible in game" unravelled into a measured constant: the game floats
  naval pawns with the mean water surface **~0.05u above the model origin**, while the preview's plane sat at
  origin height — every vessel previewed slightly high. Chased methodically: bake logs proved the −0.2 offset
  was in the shipped mesh; the runtime was exonerated (static offsets bake into the mesh, no runtime add); a
  false start blamed a stray builder part (the "floating strips" were the hull top — a real Jagdpanzer is 2m
  tall); unit scaling was ruled out by the era grid (Era5+ rows all 1.0). The decisive instrument was built
  mid-hunt: a **keel/top numeric readout** in the preview header (stale bake reads keel 0.00; wrong plane reads
  the right keel under the wrong picture) plus the user's calibrated cruiser — hull paint marking the true
  waterline — converged the constant stepwise (0.5 → 0.1 → **0.05**, the *water @* dial, EditorPrefs-stored,
  measured-on-the-map tradition like the 6.93u tile). The residual: the sub "matched only at 0.15" — that 0.1
  is **wave amplitude**, confirmed in-game (long swell dynamically claims the deck); low hulls lose real
  freeboard to crests that a flat plane can't render — the dial doubles as a crest-state preview. Verified
  in-game: the sub now rides deck-awash, superstructure clear, matching the preview. Discovered en route, on
  the audit list: the runtime-fields help text says position "applies on load" — for STATIC entries it is
  baked and silently needs a re-bake (label lie, the backlog #4 family). **Epilogue — one number, one home:**
  the constant lived in three places within an hour (EditorPrefs dial / code default / docs — the glbconv
  split-brain in miniature, user-spotted), so it landed as **pack configuration**: `waterLevel` in the
  registry header next to `unitScales`/`eraGrid` — versioned, dual-written, backed up, shown read-only in the
  preview, no UI can change it; the dial was retired the same day it was born. Mechanism explained too: the
  game's own ship meshes anchor at the WATERLINE while HAF bakes anchor at the KEEL — every vessel's negative
  Z has been re-creating the draft that convention difference removes; 0.16 = rendered-surface offset + wave
  crest. All three affected vessels' Z recalibrated (sub −0.04, cruiser −0.23, tank destroyer 0) and verified.

- **SHIP STATUS — "baked but not built" made visible (2026-08-18).** The boot pre-flight's first real run caught
  it live: the HandCrankedSubmarine re-bake (19:34) postdated the last mod build (19:29) by five minutes, so the
  game resolved a dead skeleton GUID — the exact "was it baked and shipped?" trap the validator names. Nothing
  in the editor surfaced which bakes the game hadn't seen, so now two things do: an inline notice in the Model
  Factory on the selected entry, and a **Tools ▸ HAF ▸ Ship Status** window listing every entry against the
  newest build (BAKED-NOT-BUILT / BAKE-MISSING / ORPHANED-BAKE / shipped / no-bake-needed), both driven by one
  shared core that reuses the baker's own output whitelist so it can never drift. Bonus finding from the same
  scan design: orphaned bakes (outputs left by renamed/removed entries) still ship as dead bundle weight — the
  window lists those too. **Its first run was its own drill**: the scan knew only the unit registry and accused
  every district and prop bake of being an orphan (user screenshot) — fixed by teaching it all three registries
  (units, districts, props) + hand-prop references, each row labelled with its kind; ConversionGateTest's
  `__convgate__` debris got its own TEST ARTIFACT label. Then, per three user requests in one sitting, the list
  became a cleanup tool: any row with baked outputs is selectable (plain click / Ctrl-toggle / Shift-range, the
  checkbox and Tick all drive the same state), and **Delete selected** sweeps via the baker's whitelist with the
  delete-guard snapshotting every file — owned entries are only un-baked, never removed. Full page:
  docs/Ship-Status.md. Same day: the preview's tile hex went **double-sided**, so a boat's waterline stays
  visible from below the surface — user-verified as the way to judge how deep a vessel should sit. (And a
  floating tank destroyer that looked like a grounding bug turned out to be the live-offset display working
  exactly as designed: a Position-offset Z of 0.5 — the waterline axis — authored back when the bake sat SUNK
  in the ground, i.e. a manual compensation that auto-ground later made redundant and turned into a float.
  **The compensating dial outlives the defect it compensated for** — same family as the 2×-height helicopter
  offset of 08-07; when a bake-level fix lands, every manual dial compensating the old behavior becomes a live
  error with no alarm. The ground/waterline reference + live-offset preview is what makes this class visible
  at a glance now — a registry-wide offset audit found all remaining verticals deliberate: flyers at altitude,
  vessels below the waterline.)

- **ENTRY-STATE COHERENCE (2026-08-18) — the "serious configuration bug" of 2026-07-26, structurally addressed.**
  An entry's config lives in four places (two window forms, the deployed registry, the project dual-write copy)
  and the reconciliation ambushed the user for weeks. Built per the backlog's recorded impact order: (1) the
  Factory gets the **Lab's Form ≠ registry banner** — surviving form compared on every reload, explicit choice
  (↻ Reload entry / Save / Bake), never a silent resync — and the cross-window nudge is now **coherence-aware**
  (a Backup-window restore raises the banner instead of silently reloading an edited form); (2) the **bake-time
  model-file confirm** — a stale form file that differs from the saved entry's asks loudly with both paths shown
  (the translation-cube-over-Jagdpanzer ambush, dead); (3) the **SelectEntry funnel** — every selection change
  (popup, Remove, Undo, banner-reload) routes through ONE path updating dropdown + form + preview + coherence
  flag atomically, structurally retiring the 08-16..18 stale-window family (whose four bugs were each one
  forgotten surface at one bypassing site; Clone is the one documented deliberate bypass). **Self-review before
  ship caught three defects**: the Lab's own spurious-banner lesson unlearned (OnGUI's `animated` self-heal must
  be mirrored onto the registry copy before comparing), an entry *removed* under the window reporting "no
  difference" (now maximal difference), and Clone inheriting a stale banner whose Reload would wipe it. The
  two-pack.json design is now documented in Factory-Manual; "Make static…" already covers the animated→static
  path. **DRILLED same day — all five drills passed, and the drill caught a fourth defect the review could not:**
  the vanished-entry banner (drill 3: Bears hand-removed from pack.json) never fired, because `RefreshList()`
  re-derives the dropdown index by name and resets it to 0 when the entry is gone — so the compare's
  `selected <= 0` guard swallowed EXACTLY the case the review-fixed `reg == null` rule existed for. Two
  individually-correct mechanisms cancelling each other: structurally unreachable, invisible to reading,
  instant under fire. Fix: the form carries its own serialized identity (`loadedName` — which registry entry
  it was loaded from / last saved as; empty for `<New>`/clone), the compare keys on it instead of the volatile
  index, and the banner's Reload uses it too (a half-typed rename reloads the ORIGINAL entry). Verified by
  re-drill; a per-reload Console evidence line (`loadedName` + differs) now makes any future missing-banner
  report diagnosable instead of guessable. The ADR's lesson, proven a second time in one week: the defect was
  in the interaction between two reviewed-correct parts — only executing the scenario finds those. **Drill
  follow-ups:** the Refresh button is now the on-demand coherence check (user design — re-reads the registry
  and raises/clears the banner immediately, no recompile needed; the form is still never touched without the
  explicit Reload choice), and the post-drill diff review caught that the drill-1 test edit (pawn `…_01a`, a
  nonexistent unit) had been SAVED during the drill flows — reverted in both registry copies, Bears restored,
  both copies verified to hold the same 23 models. Drills leave fingerprints; always sweep the registry after.

- **FIRST-SELECT PREVIEW FINALLY TEXTURE-CORRECT (2026-08-18)** — the user's "number one problem with this
  editor," deferred since 08-01: selecting a model showed it mis-textured until the next bake. **The root cause,
  finally pinned to a line:** `BuildMultiAtlasAndRemap` remaps the rig FBX's skinned-mesh UVs into the packed
  atlas **in memory only** (clones assigned onto the imported asset) — so the preview is correct right after a
  bake and reverts to ORIGINAL-UVs-vs-packed-atlas on any reimport or editor restart. Explains every symptom of
  the bug's whole history, including why the "never force-reimport the FBX" rule existed. **First attempt
  (preferring the bake's `_Preview.prefab`) was reverted within the hour** — user drill: "why is it heading up
  without a surface?" — that prefab is a display-flipped bind pose with no ground plane. **The real fix:** the
  bake already persists the remapped clone (`_PreviewMesh.asset` — same FBX-space geometry, atlas-remapped UVs);
  `LoadPreview` now *substitutes* it for the renderer it was cloned from **inside the upright, grounded FBX
  route** — correct texture, same faithful view. **Second attempt also drill-caught within minutes** (still
  corrupt): the name-based match could never fire — `CreateAsset` renames the persisted mesh to its filename —
  so the substitution silently did nothing. Final version matches by **geometry identity** (identical vertex
  count on a skinned renderer, used once) and prints a loud `APPLIED` / `NO MATCH` Console line per preview
  load, because a silent no-match is exactly how the first two versions hid their failures. **Drill-verified by
  the user: "finally it looks correct."** Three versions, two caught by drills — the ADR working as written.
  **Ship-safety re-confirmed throughout:** display-only either way — the shipped GPU mesh always carries the
  remapped UVs (`draw_mats.txt` proof, 08-01, and every in-game verification since). The preview was lying; the
  mod never was. **Why it survived six weeks of fixes — the full retrospective (six protective mechanisms,
  general lessons): [Preview-Texture-Postmortem](docs/notes/Preview-Texture-Postmortem.md).**

- **PACK PRE-FLIGHT VALIDATOR — silent content failures become named messages (2026-08-18).** The
  designed-not-built tool from the 08-02 external review, built exactly per
  [Pack-Validator-Design](docs/notes/Pack-Validator-Design.md): a wrong bone name (`muzzleBone: "Turrret"`), a missing
  WAV, an unbaked clip GUID, or an out-of-range dial used to just… not happen. ONE pure rule set in the shared
  schema DLL (`Haf.Schema.PackValidator`: ~30 rules — file existence + format, bone-name existence, pawn-name
  reality, `x,y,z`/`a,b,c,d` formats, every documented numeric range, the state-driven mutual exclusions — with a
  tri-state context: a host that can't answer a lookup SKIPS the check, never guesses), consumed by two thin
  hosts: the Model Factory's **"Validate pack"** button (pre-ship: pawn names from the Pick list, files in the
  deployed pack, bones from each entry's baked skeleton asset) and the plugin's **boot-time pass** (once per
  process after registration: bones against the LOADED skeleton, files on the *player's* disk, authored GUIDs
  that didn't resolve — appended as `## Pre-flight` to `haf_load_report.txt` with one summary log line).
  Warnings EXPLAIN, nothing is blocked — the fail-soft rule stands. 19 rule tests; suite 92 → **111**.
  **DRILLED same day, and the drill earned its keep before passing:** three faults planted in the live pack (the
  design's own `"Turrret"` bone typo, a misspelled WAV, a volume of 5) → first result "validate detects nothing" —
  a **silent failure in the Validate button itself** (no try/catch), exposed by running the same core on the same
  file headlessly (which named the fault instantly). The validator failing invisibly is the exact disease it
  exists to cure; fixed with loud exceptions, the validated registry path printed even on clean runs, and (drill
  feedback) results in a dialog instead of only the Console. Second run: **all three faults named with field,
  entry, and reason — drill passed**, pack restored byte-identical. The ladder held again: written → reviewed →
  drilled → trusted.

- **AUTO-VERSIONING + DELETE GUARD (2026-08-17, user: "auto backup, especially when I remove assets… also
  configuration… go back versions").** Two silent, optional guards in `BackupAuto.cs`, both feeding the same
  restorable backups list: a **delete guard** (an `AssetModificationProcessor` snapshots any asset under the
  protected roots to `_deleted_<timestamp>/` BEFORE any deletion — Factory Remove, Project-window, script — then
  lets the delete proceed) and a **daily auto-version** (first editor load of the day runs the full backup — assets
  AND configuration — through the same core as the button, so it gets a Restore button like any manual version;
  newest 3 kept, rotation logged; manual/_deleted/_prerestore never auto-deleted). Stricter side effect: a
  COUNT-MISMATCH backup now aborts a restore's pre-snapshot and skips the offsite zip instead of proceeding on a
  suspect archive. Headless-compile-checked (Roslyn gate). **Critically reviewed the same hour, four real
  defects fixed pre-ship:** (1) guarding `Assets/Resources` would have FLOODED the backup root — the bake
  pipeline delete-firsts baked assets on every re-bake (~30 `AssetDatabase.DeleteAsset` sites) — dropped from the
  protected roots (bakes are regenerable; the daily auto still versions them); (2) same-second deletes of
  `Tank.png` + `Tank.mat` collided into one folder, silently overwriting the first manifest — extension kept +
  counter-uniquified; (3) the 1+ GB daily auto copied synchronously on editor load (~30-60 s "hang") — moved
  wholesale to a worker thread (pure file IO); (4) delete-guard snapshots had no `SRC` manifest, so their Restore
  button was dead — now a one-click restore incl. the `.meta` (GUID preserved, references survive). **And a
  fifth, user-spotted during the recovery drill: restore was ALL-OR-NOTHING** — recovering one group from an
  older snapshot rolled every other group back to snapshot time (an old backup got more dangerous to restore the
  older it grew). Fixed with **selective restore**: the same group checkboxes that scope a backup scope a
  restore; the confirm dialog states the scope; `_deleted`/`_prerestore` snapshots still restore whole. **The
  drill kept giving: two more user-found issues, fixed live.** (a) Remove left the PREVIEW rendering the removed
  model (stale-state, same family as the sel-reset bug) — cleared. (b) Recovery required knowing the Backup
  window exists and having a backup that happened to cover the moment — Remove is now **recycle-bin semantics**:
  it snapshots the entry JSON + the exact baked-output whitelist to `_removed_<ts>_<name>/` BEFORE deleting
  (aborts if the snapshot fails — never destroy what can't be restored), and an **Undo remove** button appears
  right where Remove is (user-designed placement), restoring registry entry + baked assets in one click. **And a
  third: "restored 1628 files!!!!"** — the blanket copy alarmed exactly the person it was reassuring. Restore is
  now **smart**: byte-compares each file and writes only the missing + actually-changed ones (identical files
  untouched — also sparing Unity ~1,600 pointless re-imports), reporting all three counts. **And the drill's
  biggest catch, found the hard way ("the restore FAILED!!!"): the backup NEVER CONTAINED the model registry.**
  The config group still captured the pre-multi-pack `haf_*.json` root files; the registry moved to
  `haf_packs/<mod>/pack.json` and the group was never updated — so the restore brought back all 28 baked files
  but had no registry entry to restore. Recovered by re-inserting the entry verbatim from the git-tracked
  project registry copy (both registries re-validated: 22 models, parse-clean); `haf_packs/` added to the
  Runtime-config group so every future backup carries the real registry. The honest lesson: a backup's contents
  were asserted from its group NAME, not verified — the same claim-vs-check gap the smoke test was built to
  close, now closed for backups too. **The drill's final round (same evening):** critical-content verify (a
  backup missing the registry marks itself NOT ok; green says "registry verified in snapshot"); `_removed_`
  snapshots fully restorable from the window itself (shared core with the Factory's Undo button, which now also
  selects + loads the restored entry); the list grouped into counted foldouts with date-time-first rows
  (delete-guard open by default, user-tuned); preview-scratch churn (`_PropFit`/`_Preview*`) excluded from the
  delete guard; restores auto-refresh open Factory windows (the restore "didn't work" — it had; the dropdown was
  stale); tooltips on every button; the list fills the window height. **Thirteen user-driven fixes in one
  drilling session — and the process lesson became an ADR: a tool is not trusted until it is DRILLED.**

- **OFFSITE BACKUP — the last total-loss scenario closed (2026-08-17).** The Backup window gains an optional
  *Offsite folder*: every backup is also written there as ONE `HAF_<timestamp>.zip` — silent (background thread,
  a multi-GB FactorySource snapshot no longer freezes the editor), atomic (`.partial` → rename), never
  overwritten, and self-verifying (the zip is re-opened and its entry count compared against the snapshot; a
  mismatch deletes the partial loudly). Point it at a cloud-synced folder and the licensed source models + bakes
  — the only irreplaceable, un-git-able half of the project — survive a machine-level event. Auto-zip toggle for
  set-and-forget; a manual button covers pre-existing snapshots; `_prerestore` safety snapshots deliberately stay
  local. (Editor-side; compile-checked headlessly via the Roslyn gate, whose `.rsp` gained the
  `System.IO.Compression` pair + a defensive `Assets/csc.rsp`.)

- **SHARED-SEAM CENSUS — the first mod-conflict guard (2026-08-17).** Pack-vs-pack conflicts were always guarded
  (declared overrides, first-loaded-wins, loud logs — ADR'd and test-pinned); HAF-vs-OTHER-MODS had hygiene
  (postfix-first, conditional prefixes) but zero visibility. The smoke test now walks every method Harmony knows
  is patched, keeps OURS, and names any that another owner also patches — `"AnimationLoad (also com.other.mod)"`.
  Informational by design (a neighbor isn't an error; Harmony stacks safely) but it's the pre-printed suspect
  list for the day an interaction bug appears. The PASS line gains `N patched seam(s) [M shared]`. Tested for
  REAL: the suite patches a dummy method with two live Harmony instances and asserts the foreign owner is named
  (which pulled Harmony's MonoMod/Cecil runtime deps into `References\` + `fetch-refs.ps1`). Suite → **92**.

- **F8 WINDOW: no more click-through or reflowing text (2026-08-17).** Left-dragging the window panned the map
  under it — the game reads mouse input independently of IMGUI. Fixed WITHOUT camera surgery by speaking the
  game's own language: type-hunting the Managed DLLs (bindcheck-style MetadataLoadContext) found
  `Amplitude.UI.Interactables.UIInteractivityManager.IsMouseCovered` — the public static the game's own windows
  set so map input ignores covered drags. `Hk_MouseCoverExtend` postfixes `SpecificUpdate` (where the game
  recomputes the flag each frame) and ORs in "or over the HAF window" — every consumer that respects the game's
  windows now respects ours. Binding catalogued (bindcheck `50/50`). Also pinned the window to a fixed 520px
  width: GUILayout re-measured width from content every repaint, so the verdict text visibly re-wrapped while
  dragging — deterministic wrap now.

- **F8 SMOKE TEST DEPTH PASS — per-entry assertions, each earned by a shipped bug class (2026-08-17).** The
  in-game smoke verdict was a coarse gate (bindings ok / error count / models > 0); user verdict: "add more tests
  to make it really meaningful." It now also asserts, per INJECTED entry: **dead clip roles** (a role GUID
  authored in the registry whose animation never resolved — the howitzer's "shipped a dead idle-override GUID"
  becomes a named FAIL instead of a unit quietly failing to deploy), **missing assets** (skeleton, or an authored
  atlas that didn't load — the organ-gun-red class gets a named cause), **failed configured sounds** (checked
  once the audio poll has tried), and a **GPU-wall alarm** (any mesh layer ≥95% verts/indices — the silent
  skin-vanish wall, alarmed before it hits, via a structured `ReadMeshBudget` now shared with the F8 display).
  The verdict stays a pure function (`SmokeFacts` → `SmokeVerdict`) so every new fail class is unit-pinned —
  a PASS now states what it checked ("deep checks clean on N injected"). **The first live run earned its keep
  twice**: it flagged `Retex_…StealthCorvettes` "missing skeleton" — a FALSE POSITIVE (a retexture-only entry
  legitimately has no skeleton; corvettes verified fine in-game), which forced the per-entry gathering into a
  pure `GatherEntryFacts` with every asset check gated on *authored* GUIDs — and the refactor's first draft
  itself shipped the exact `cb`/`cbb`-class wiring typo the review had warned about (`e.ald` doubled, `e.alc`
  dropped). Both are now test-pinned: a retexture-entry case plus a **36-component wiring theory** asserting
  every GUID component of every role arms its dead-role check alone. And because an instant PASS "didn't feel
  like real testing" (fair — the deep pass reads outcomes the load pipeline already established, so speed is
  inherent), the PASS line now **shows its work**: it prints how many facts it verified ("verified 47 clip
  role(s), 17 asset(s), 12 sound(s), 3 GPU layer(s)") — auditable against the registry instead of asking to be
  believed. **Scale-out (same day):** the deep pass now also covers the axes the smoke test never looked at —
  **districts** (per `haf_districts.json` entry: fxMesh GUID parsed, authored ground-material NAME resolves;
  live tile count in the PASS line), **texture-only retexture skins**, and **hand props** (authored →
  layer + atlas must exist). All data-driven off the registries, so every future unit AND district is covered
  the day it's added, no test code. Fault-injection round proven live the same day: a flipped atlas-GUID digit
  and a renamed WAV both came back as named FAILs on the first F8. **Loose-file sweep (same day, user: "basically
  any loose file"):** every disk file any entry references (all 7 sound roles + the skin PNG) is now
  existence-checked for ALL entries — injected or not — with the loaders' exact search order (pack `assetDir`
  first, legacy shared dir second), closing the hole where a missing WAV for a unit absent from the current save
  smoke-tested green; a missing-on-disk file reports once (the derived load-failure line is deduped). Suite
  63 → **90**.

- **CI — every push now builds + runs the full suite, with zero game files (2026-08-17).** The blocker was
  always the gitignored `References\` DLLs; the unlock was discovering the `Amplitude.Mercury.Animation.dll`
  reference was **vestigial** — every Amplitude touch in the plugin is string-based reflection, so the csproj
  reference was simply dropped and the build stayed green. Every remaining reference has a public home:
  Newtonsoft 11.0.1 (nuget.org), `BepInEx.dll`+`0Harmony.dll` (the official BepInEx 5.4.21 release zip), and
  the UnityEngine modules from **unity.bepinex.dev** — BepInEx's mirror of *runnable* unstripped Unity
  assemblies, version-exact 2021.3.1 (the nuget `UnityEngine.Modules` reference assemblies compile but throw
  `TypeLoadException: internal call with non-NULL RVA` the moment tests load them — found the hard way, 52/61
  red). `tools/fetch-refs.ps1` collects all 12 DLLs (never overwriting game-copied ones; game copies win), and
  `.github/workflows/ci.yml` runs fetch → build → 61 tests on a clean runner. Proven by full local simulation
  first: fresh clone, no References, fetch, build green, **61/61 pass**. bindcheck stays manual — validating
  bindings genuinely needs the game's own DLLs.

## Units & animation

- **WHEEL SPIN for state-driven vehicles (2026-08-22, the towed howitzer — verification pending).** Its clips are
  fold/folded/unfold POSES, so nothing rolled the wheels while it travelled (and the aim-layer sanitize zeroes the
  game's own wheel channel on our rigs). New shared-schema fields `wheelSpinBones` ("l_wheel@0;r_wheel@0" — baked-rig
  substrings + roll axis, max 3) and `wheelSpinDegPerUnit` (360/(2πr)); `ApplyWheelSpin` (Pose.cs, non-donor path
  after the gun elevation) resolves the bones once like the rotor reclaim and writes BoneRotation slots 0..2 with
  angle = DISTANCE × deg/unit — the distance is a new odometer on the pawn's turn-ease state (`TurnState.travel`,
  `TryTravelAt`), so the wheels stand still during the fold-in-place hold and roll exactly with travel. Factory:
  "Wheel spin — bones / deg per unit" under Flight character (+ the ownership list; hand-list gate PASS). Regex
  fallback covered (parity PASS). Set on TowedGunHowitzers in the live pack.json and the repo mirror (165 deg/u —
  a 0.35 u wheel guess; dial from the drill). First drill: bones resolved (`l_wheel -> 14`, `r_wheel -> 17`) but
  nothing turned — the howitzer has `clearAimLayer`, and `ClearAimLayer` ran AFTER the wheel write in the same frame,
  flattening all four slots. The write moved into `ApplyAnimatedPose` right after the clear/turretize/sanitize step
  (animated entries only — the only ones with a pose to roll wheels under). Second drill: STILL nothing. The user
  called it: "let's make it work in the Animation Lab first" — so the wheel roll is now BAKED, Lab-native. ENCReload:
  `deploy_convert.py` step 7c and `add_role_clips.py` take `wheelBones axis frames degrees` and key a LINEAR roll of
  the wheel bones into the `folded` role clip (N+1 frames, frame 0 = the folded rest; axle = the bone-local axis
  closest to the world axle, AUTO = each wheel's thinnest skinned extent, signed — vehicle_rig.py's fast-path math);
  `add_role_clips.py … wheelsonly` retrofits an existing GLB rebuilding ONLY `folded` (the howitzer's converter-tuned
  `recoil` and legacy `deploy[...]` slices must survive). Registry fields `deployWheelBones/Axis/Frames/Degrees`,
  Lab UI "Wheel bones (roll while moving)" + picker (hand-list gate 44/60 PASS), baker argv[17..20]. Run headless on
  the howitzer GLB: `{'howitzer:l_wheel': '+X', 'howitzer:r_wheel': '+X'}, 15 frames, -360 deg`; its registry entry
  now has Movement = `folded[1..15]` and the runtime wheelSpin keys removed (no double roll). **Proved end-to-end
  through the real pipeline**: the user's Bake re-ran the converter with the new args, and a Blender probe of the
  regenerated `deploy_converted.glb` reads `folded` = 16 keys per wheel bone with the quaternion w running 1.0 →
  −1.0 (a clean linear 360°) while `deploy`/`fold`/`unfold` keep their 2 static keys.
  **The runtime aim-layer route is DELETED** (schema fields, regex, `ApplyWheelSpin`, the `TurnState` odometer,
  the Factory fields): two drills never saw it move a wheel, the baked route does the job, and an unproven knob in
  the shared schema + a Factory field that does nothing is exactly the silent lie this project keeps hunting. One
  route: bake it.
  **THE WRONG UNIT, then the wrong AXIS.** Two more drills reported "wheels still don't turn", and the log finally
  named the unit that was moving: `[State] poll: 'SiegeHowitzersCar' … moving False -> True` — the user was driving
  the **Era 5** siege howitzer (`animClipMove: deploy[0..0]`, a held pose), while the wheels had gone onto the
  **Era 6** TowedGunHowitzers, which never moved once in any of those sessions. Era 5 now carries the same wheel
  config. Chasing the wrong unit also produced two changes to movement detection (holder transform instead of
  Pawns[0]; a speed-independent test) made on false inference — both REVERTED, unverified changes don't ship.
  Then, on the right unit: the wheels rolled but swept **through the air**. Offline probes cleared the bake input
  (bone pivot exactly at the wheel centre, offset 0.000; the wheel's skinned centroid held still across the clip —
  it spun in place). What did not survive the bake was the AXIS CONVENTION: the roll was authored about "the
  bone-local axis closest to the world axle", but a bone's X/Z axes are re-derived downstream from head→tail while
  Y — the bone's own direction — survives. Fixed by adopting the Vehicle Lab's convention, whose wheels do spin
  correctly in-game: `_orient_wheel_bones` points each wheel bone's TAIL along its axle (head untouched, so the
  pivot stays at the wheel centre and the mesh cannot move at rest), then keys the roll about that bone's local Y.
  Mirrored in `add_role_clips.py`. Verified offline on a real conversion: centroid fixed at (19.49, −0.05, 1.39)
  for the whole loop while a marker vertex sweeps top → front → bottom → top. **Verified in-game 2026-08-22: the
  wheels roll, forward** — but about a pivot at GROUND level, sweeping underground. That was MY re-orientation:
  giving the wheel bone a non-identity rest rotation makes Amplitude's skeleton bake mangle its offset. Measured
  from the baked `_Skeleton.asset` (plain YAML — compose each bone's `Local` up its `ParentIndex` chain): the legs,
  rest rotation identity, come out symmetric (`l_leg` y 0.5851 / `r_leg` y 0.5751); the wheels, rest rotation
  `(0.5,-0.5,-0.5,-0.5)`, come out 1.86 apart in HEIGHT (`l_wheel` y −0.4038 / `r_wheel` y +1.4612) — one pivot
  below the hub, one above. The FBX was clean either way (`local T = (21.096, 0, 0)`), so only the baked asset
  showed it. Reverted: the roll is keyed about the local axis nearest the axle with the bone's rest UNTOUCHED
  (re-verified on a real conversion: identity rest rotation, pivot at the hub, centroid fixed through the loop).
  Then, on the right rest, the game STILL rotated the wheels about a point at ground level — this rig's compensated-scale chain displaces the composed pivot. Rather than guess a compensation, it became a DIAL at the user's request: `deployWheelLift`, in tyre radii (0 = the true rest, 1 = up one radius), moving head AND tail so the rest ORIENTATION stays identity. **The lesson that matters: verify the BAKED asset offline** — the skeleton YAML answers "is this pivot right?"
  in seconds, where four in-game drills could only say "it still looks wrong".
  Direction is the sign of `deployWheelDegrees` (the earlier build rolled forward at `360`)
  — and it must be set IN THE LAB: editing pack.json while the Lab holds that entry open loses the change, because
  its Save writes the stale in-memory value back and the next bake uses it. That cost a whole bake cycle: the flip
  landed in the file at 12:12:59, the Lab overwrote it, and the converter ran at 12:11:30 with the old sign.
  **OUTCOME: the wheels are OFF on both howitzers, and the reason is documented as a limit, not a bug.** The user
  read it off the preview — "the model is fundamentally baked wrong, it shows this in the preview" — and the bbox
  measurement proved it: EVERY clip of this converted model poses it **90° rotated from its own rest pose**
  (rest `(52.1, 135.7, 37.6)` vs `folded` `(41.7, 27.6, 119.3)`), the legacy clip at 2× scale, which is where the
  skeleton's `Scale 2` / `BindPose 0.005` compensation comes from. Pawn-level features are blind to it (that is why
  fold-before-moving, deploy-on-stop, the bombard hold and turn-in-place all work); bone-level ones inherit a frame
  that disagrees with the geometry, so no authored wheel roll can pivot correctly. The sharp rule, now in
  Animation-Pitfalls: **a converted rig carries motion the source already had (the T-62's wheels DO spin), but you
  cannot author new bone motion into it.** Both registries reverted to `deploy[0..0]`; the `deployWheelLift`
  pre-compensation hack was removed (treats the symptom); the wheel tooling stays for rigs whose clips and rest
  share a frame. Real repair, with an offline acceptance test ready: make `deploy_convert` author clips in the rest
  pose's frame (`folded` f1 bbox must match the rest bbox).
  **AND THEN IT SHIPPED — via the Vehicle Lab, verified in-game the same afternoon.** The user called the route
  ("let's recreate the model properly in the Vehicle Lab, start simple with only the wheels"), and the generated rig
  passes every check the converted one failed: clip frame **identical** to the rest frame `(52.1, 135.7, 37.6)`;
  wheel pivots **symmetric** at the hubs (`±0.9325, 0.5424, −0.001`); **every scale 1**; four bones instead of 28.
  Recipe on the Era 5 SiegeHowitzersCar (kept the Era 6 gun intact as the fallback): static source
  `m114_gun_only.glb` (no armature, no crew) → mark the two ROAD wheels W — the auto-guess also grabs the crew's
  hand-cranks by name, and a near-cubic bbox would give them a meaningless AUTO axle → Generate rig → Factory/Lab
  with Deploy conversion OFF, Convert raw rig ON, Fix 100× OFF, Auto-ground ON, Idle stance `Spin[0..0]`, Movement
  `Spin`, every `deploy…`/`recoil` clip field cleared. **"In game it moves perfectly."** Cost, as agreed for step 1:
  the fold/deploy/recoil are gone on that unit until re-authored on the clean rig.
- **IT TURNS, *THEN* IT FIRES (2026-08-22, verified in-game).** The recoil kept playing before the gun had slewed,
  and it took five attempts because four of them were diagnosed by reading code rather than the log. The cause was
  a **race**, visible in the line ordering of the very first failed run: the ranged-fight hook arms the fire, and
  the strike registers its aim override **20 ms later**. In that window the unit has not been told to turn, so
  `TurnMisalignAt` legitimately returns 0° and `TryAimRelease` finds nothing — the hold released instantly on
  "aligned", and the strike then announced a 173° turn. Every earlier fix governed what happens *after* the hold
  engages (arming HELD via `EffectiveTurnRate`, re-checking alignment at the deadline, releasing from the attack
  event), so all three read as no-ops while the hold was ending 20 ms in. *"Aligned" is only meaningful once there
  is an aim to be aligned with* — an unknown aim now holds the fire for a 0.5 s grace, bounded by the same 4 s
  failsafe. The measurement that found it was two lines: elapsed-time plus measured misalignment at the moment of
  release, and at the frame the attack clip actually starts. **Lesson recorded: a fix that changes nothing is
  evidence that the assumption under it is wrong — stop fixing and add one measurement.**
- **THE HOWITZER KICKS (2026-08-22, verified in-game).** *"Alright, it finally works."* The M114 rebuilt on a Vehicle
  Lab rig now rolls its wheels, folds before it turns, spreads its trails on arrival, raises to 45°, waits for the
  turn, and **recoils when it fires**. Getting the last part took two distinct bugs and four wrong theories.
  **Bug 1 — a bone's own translation does not render.** The clip baked the slide correctly and the engine's *own*
  `GetPoseTRS` decoded it correctly (`SLID 0,3 (0,0,-0.001)->(-0.001,-0.013,-0.301)`, matching the authored frame 8
  to three decimals) and the barrel still did not move a pixel. That is Law 5: bone positions are held at BIND and
  only orientations propagate. `deploy_convert` had the verified finding written down the whole time — *"a bone's OWN
  local translation is DROPPED ... but a bone's position derived from an ANCESTOR's rotation DOES bake"*. So the
  slide is now an arc: a hidden **`RecoilArm`** pivot 228 units off the bore with `Barrel` under it, rotated by
  `θ = slide/R`. At peak the muzzle travels −11.87 along its own bore (asked 11.96), 3.4 off-bore, 3° residual tilt.
  The far-pivot trick is not a legacy workaround for missing translation support — **it is the mechanism**.
  **Bug 2 — the recoil armed UNHELD.** The gun then kicked at the instant the order was given while the muzzle flash
  correctly waited: the two halves of "turn, then attack" asked different questions. `ApplyTurnEase` resolves a rate
  as per-model → **category** → global; the fire arming asked `turnRate > 0f || e.turnRate > 0f`, which skips the
  category dial, so a land unit eased entirely by `land=180` never armed `waitAlign`. Extracted as
  `EffectiveTurnRate(e)` and used by both. Two further holds were tightened en route: the attack-pose deferral
  skipped its alignment re-check whenever the strike clock supplied the deadline, and the recoil's own release used
  that clock exclusively — both now require the clock elapsed **and** the pawn actually aimed, still capped at 4 s.
  Finally a **Recoil lead-in** dial pads the front of the clip, because the engine decides when the clip *starts* and
  its clock is only an estimate; the front of the clip is the part we own outright.
- **WHAT THE PROVEN RECOIL ACTUALLY DOES — and a flip-flop worth recording (2026-08-22).** The kickback did not play
  in game. Reading *"BIND must equal animation frame 0"* out of the engine contract, I blamed the `Recoil` clip's
  non-identity frame 0 (it held the deployed pose so the gun would not fire from its travel pose) and reverted the
  hold. That was **wrong**, and the turntable said so immediately — the gun went back to firing horizontal. The
  proven M114 settles it empirically: `deploy_convert`'s `make_role` writes **absolute poses with no delta-rebasing**,
  and its `recoil` role is authored from `m_home` captured at `deploy_end` — *the deployed pose*. The bind==frame-0
  contract governs the **primary (Idle/reference)** clip, which defines the reference pose; a **role** clip
  legitimately encodes a non-identity pose against it. Law 2 is the same point from the other side: the stance
  belongs in a role clip, never the primary. Hold restored. The real cause of the in-game failure is still open, so
  `[AnimDiag]` now covers the **attack** role and reports **translation** (`SLID d (t0->tm)`, via the engine's own
  `GetPoseTRS`) — the one motion, on the one role, the scan could not previously see. Lesson: two opposite changes on
  two readings of the same paragraph is a sign to go and measure, not to read it a third time.
- **RECOIL: THE BARREL FINALLY GETS ITS OWN BONE (2026-08-22).** "Of course `keepTranslations`, what else?" — and
  that is the point: the flag exists, so recoil can be an **honest slide** instead of the far-pivot `RecoilArm`
  rotation trick `deploy_convert` was forced into for want of any translation at all. New **Recoil (fraction of
  tube)** + **Recoil frames** dials. Marked **Gun** parts move onto a new `Barrel` bone, a child of `Gun`; marked
  **Cradle** parts stay — the split the Cradle role was invented for, one step after it was named. The fraction is
  measured breech→muzzle, not trunnion→muzzle, so turning *Gun pivot* cannot silently change what the number means.
  **Identity rest, deliberately:** the obvious rig points the bone down the bore so recoil is `−Y`, but a
  non-identity rest is mangled by the skeleton bake (the measured `(21.096,0,0)` → `(-0.00932,0,-0.00466)`), so
  `Barrel` is axis-aligned like every other bone here and slides by a local translation along the rest-frame bore —
  which, being a child of `Gun`, rotates with the elevation for free. **Verified offline:** `0.00°` deviation from
  the bore both level and elevated 45°, constant 23.93-unit slide, and *only* the tube moves — cradle, trails,
  wheels and hull all sit at `+0.00`. With recoil off the bone list is identical to the shipped M114 rig, so every
  gun already baked regenerates unchanged. The kick is a derived ~15% of the clip with the ride forward taking the
  rest (2 frames back, 14 forward at 16) — the asymmetry is what reads as a shot, so it is not left to be set wrong.
  One measurement error caught in the making: an early check reported 8° of bore deviation under elevation, which
  was the *test* picking different min/max-Y vertices once the tube rotated, not the rig.
- **CRADLE — THE PART THAT HOLDS THE CANNON, GIVEN ITS PROPER NAME (2026-08-22).** The user had marked `cannon2` as
  *Muzzle* — "I called it the muzzle because it's one part" — and the run's own readout caught it: `tip=(-0.00,
  -34.14, 19.25)`, pinning the muzzle 26 units short of the real one at `Y −60.55`, which silently shrank the
  breech→muzzle span from 76 to 49.6 and made a *Gun pivot* of `0.2` actually land at 11% of the tube. The label and
  the number had stopped agreeing. Their own description — "the part that holds the cannon" — is exactly what
  artillery calls a **cradle**, so it got the name rather than a workaround, on the same reasoning as Trail-not-Leg.
  The three gun roles now all weld to the one `Gun` bone (they elevate together about the trunnions) and differ in
  what else they mean: **Gun** is the tube and *defines* the span; **Cradle** is the frame holding it and is kept
  **out** of the span, because a cradle stops short of the muzzle and would shrink it; **Muzzle** is a separate brake
  and *pins* the tip. The split earns itself twice over on recoil — the user's own read, *the barrel is the part that
  kicks back*: Gun moves, Cradle stays. Proven headless three ways on the same marking: as Muzzle the span truncates
  to `−34.14`; as Cradle it is the honest `−60.55`; as Gun it is byte-identical to Cradle, since this barrel already
  outreaches its cradle both ways — so the role changes nothing *today* and is still the right home for tomorrow. All
  three keep one Gun bone with 5 parts welded, no bone added.
- **A MUZZLE ROLE THAT IS DELIBERATELY NOT A BONE (2026-08-22).** Reading the howitzer's anatomy off the mesh rather
  than the part names settled what the gun actually is: `barrel1` is the 76-unit tube — and its last 6 units flare
  from 3.84 back out to 5.22, so **the muzzle brake is modelled *into* the barrel**, not as a part — while `cannon2`
  ("cannon_body") stops 26 units short of the muzzle and is the **recoil cradle**, the trough the tube slides in and
  where the trunnions live. The user's read of which part matters was exactly right: *the barrel is the part that
  kicks back*. That is the split that governs recoil — tube moves, cradle stays — and it is *not* the split for
  elevation, where tube and cradle rotate together about the trunnions and belong on one bone. Asked for a **Muzzle**
  part role anyway, and it earns its place on other models: a separately-modelled brake gets **no bone of its own** —
  it is bolted to the tube, so the rigger welds it to the `Gun` bone and it elevates and recoils with it. What the
  marking buys is an exact tip: the breech→muzzle span *Gun pivot* measures against stops guessing at the gun bbox's
  far extreme, and the run reports the **measured fire origin**, gun-bone-local — the value the Animation Lab's
  *Muzzle offset* dial otherwise costs an iterate-value-then-relaunch loop to find. Proven headless both ways: with
  nothing marked the rig is byte-for-byte the earlier one (same head, breech and muzzle), and with a part marked it
  welds onto the *one* Gun bone — "2 gun part(s) on one Gun bone", no bone added — while the tip snaps to the marked
  geometry.
- **THE GUN COMES UP WITH THE TRAILS (2026-08-22, verified in-game).** Next step on the rebuilt howitzer: get the
  barrel to raise. Shipped settings, settled by eye against measurement: **Gun pivot 0.25, raise 45°** over a
  20-frame `Deploy`, trails at 28°, `gunElevMax` 0. **"And it moves perfectly."**
  Two findings shaped it. First, HAF already elevates guns at runtime — `ApplyGunElevation` resolves `turretBone`
  else `muzzleBone` and raises it distance-proportionally during a bombard — so this needed **no plugin code**, only
  a clean `Gun` bone to aim it at. Second, the runtime writes a **`BoneRotation` slot**, a channel the clip pose never
  touches, which is *why* a baked raise and the runtime raise **compose** instead of fighting: the clip sets the base
  firing elevation, the runtime adds the per-shot lift on top. So both got built. **Gun pivot (breech→muzzle)** slides
  the `Gun` bone's head along the assembly — that head *is* the trunnion, since the bone turns about its own origin,
  and at the historical bbox-centre placement a tube see-saws about its middle and drives the breech down through the
  carriage. Measured offline on the M114's 76-unit tube: at `0.4` the muzzle rises 15.5 and the breech drops 10.3,
  with the model's lowest vertex unmoved at `Z −1.0`; the `0.5` default is kept so every rig baked before the dial
  regenerates identical. **Gun raise on deploy (deg)** then keys the elevation *into the `Deploy` clip*, on the same
  frames as the trail spread — the user's own read of the old converted model, "raising the gun used to be part of
  the deploy animation, which makes sense", and it is: a towed gun travels clamped level over its closed trails and
  comes up only once they are planted. Every use the state machine already makes of `Deploy` carries it free — unfold
  raises, `Deploy[N..0]` lowers it onto the travel lock before the unit rolls, `Deploy[N..N]` holds it up. Axis is the
  world horizontal perpendicular to the tube; the sign is *chosen* by testing which way lifts the muzzle, the same way
  the trails choose theirs. The converted M114 did bake elevation into its deploy clip too (`deployReadyFrame`) — but
  out of necessity, having no way to carry authored bone motion at all. Here it is two keys on a clean rig.
- **THE HOWITZER, REBUILT: WHEELS + A SPLIT-TRAIL DEPLOY ON A LAB RIG (2026-08-22, verified in-game).** Once the
  converted rig was shown unable to carry authored bone motion, the user called the rebuild — "let's do this properly
  in the Vehicle Lab" — and it went the whole way in one sitting: wheels first (in-game verified), then the deploy.
  New **Trail** part role — the artillery term for a split-trail carriage's arms, each ending in a spade; **Leg** is
  deliberately reserved for a walking mech limb, at the user's call. The rigger hinges one bone per trail at its BODY
  end (picked geometrically as the arm's extreme nearest the body bone, not the bbox centre a wheel uses) and authors
  a second action, `Deploy`, swinging them open about the vertical — the direction CHOSEN per arm by testing which way
  moves the spade away from the centreline, so mirrored arms open together whatever way a source faces. Dials: Spread
  (deg) + Deploy frames, saved into recipes. Verified headless before any bake: hinges fixed at `±10.07, −4.91, 17.12`
  through the clip while the tails swing `|x|` 8.52 → 14.59 → 20.24, level and symmetric. One rig now feeds the whole
  state machine — Idle stance `Deploy[N..N]`, Movement `Spin`, After-move `Deploy`, Pre-move `Deploy[N..0]` — so the
  gun parks deployed, folds before it turns (the pivot hold waits for the fold), rolls its wheels while travelling and
  opens again on arrival. The turntable also gained a **clip picker**, so `Spin` and `Deploy` can be judged before a
  bake. Spread caps near 28° on this model before the arms clip the wheels — its hinge sits slightly forward of the
  axle where the real M114 pivots aft of it; a hinge-offset dial would buy the realistic 45–60°.
- **VEHICLE LAB: A CHECKER SKIN FOR THE TURNTABLE (2026-08-22).** "I can't see the wheels spin" — the preview
  renders the raw model with no material, and a featureless grey disc gives the eye nothing to track: spinning and
  still look identical. A **Checker** toggle (preview toolbar, on by default) paints the instance with a
  high-contrast checker, which beats the real tyre texture — a tyre is itself nearly rotationally symmetric.
  Originals are remembered per renderer so toggling off restores the import exactly, and the yellow part-highlight
  still wins on top. Same blind spot, same week, as the Animation Lab's bind-pose preview.
- **PLAY THE CLIP IN THE LAB PREVIEW (2026-08-22).** "In the movement clip view it's impossible to see the wheels
  move because it's missing a texture" — true twice over: the Lab's model preview draws the rig's BIND POSE
  (`DrawMesh` of `sharedMesh`, no skinning, so no clip can ever move in it), and the raw-model ▶ picker scrubs the
  UNTEXTURED source. The preview now has a **Play clip** row: pick a baked role FBX (anim/, anim_move/, anim_after/,
  anim_premove/, anim_attack/) and it runs textured and skinned in the same view — Pause, scrub, speed (0.1–2×).
  Implementation is the Vehicle Lab turntable's proven route, ported: `Instantiate`, `PreviewRenderUtility.AddSingleGO`,
  `clip.SampleAnimation` per repaint, framed once from the posed instance, the bind-pose draw list suppressed
  while it plays, self-repainting only while running. Wears the baked atlas material and the atlas-UV substitute
  meshes — but only substitutes ones that carry matching skinning data (a UV clone without bind poses would render
  the model as an unskinned heap; motion beats exact UVs here, and it says so in the status line). Survives a bake
  (rebuilt with the fit preview) and a domain reload (rebuilt at Layout, when `cur` is loaded again).

- **PIVOT IN PLACE — ground and naval units turn first, then move (2026-08-22, built + unit-tested, NOT yet
  drilled in-game).** Turn ease smoothed the facing but the game keeps translating the pawn from the very frame it
  re-points it, so a tank ordered 150° around slid sideways into its new heading while already rolling — the user
  called it out on closer inspection of ENC. `ApplyTurnEaseCore` now takes a `pivot` eligibility flag (every
  category except `hover` and plane — a helicopter yaws while it translates): a heading change of at least
  `turnPivot` degrees **parks the RENDERED position** on the turn-start spot (last frame's game position, stored on
  the same position-joined `TurnState`) until the eased yaw is within 8° of the target, then **catches up** to the
  game's live position at 1.5× its measured pace (≥ 2 u/s) and releases on arrival. Never while a strike aim is
  armed; a 4 s failsafe and a >12 u jump release unconditionally; a second big turn mid-catch-up re-parks where the
  unit is *drawn*. Dial: `pivot=<deg>` in `haf_turnease.txt`, **default 90** (the first turn-ease key with a
  non-zero default — `TurnEaseDial.Pivot = 90f`, so an old file keeps it), `pivot=0` off; echoed in the `[TurnEase]`
  line. Tests: the every-key parse and a 4-case default/explicit-zero theory (456 total).
  **Drilled 2026-08-22: pure vehicles verified** — first finding: the artillery's **servant crew walked off** toward
  the destination while the gun was still turning (human category, rate 0 → no turn state → nothing to park).
  Fix: `ApplyPivotFollow` — a non-eased vanilla pawn standing on a parked unit's spot is drawn displaced by the
  parked unit's own `pivotVis - pos` offset (no state of its own; holds formation, releases the frame the gun does).
  **Crew verified in the second drill.** Then the per-unit override — first built as a Model Factory / schema
  field, then **moved to the Formation Override unit link** on the user's call ("that way we can also configure
  vanilla units"): `turnPivot` on the link (0 = absent = the dial under the category rule; > 0 = own threshold —
  `1` = always turn fully first, even a helicopter; < 0 = never), parsed into `FormationOverride.TurnPivotByUnit`,
  mapped to descriptors by `SweepTurnLinks` (`pivotByDesc`, the same core-token matching the Turn ease link uses)
  and resolved by `PivotThresholdForDesc` for vanilla pawns AND our entries (an entry's descId is its unit's
  vanilla descriptor). `ApplyTurnEaseCore` takes the threshold in degrees instead of a bool. The schema field,
  regex key, validator rule and Factory field were taken back out so there is ONE route (parity PASS, 456 tests).
  ENCReload: `FormationRegistry.turnPivot` + a Default / Custom angle / Never popup under the Turn ease row.
  Lesson re-learned on the way: an Edit whose old_string starts with a newline eats the NEXT line's break —
  three joined lines, caught by the build.
  **Third drill finding — it turned toward the DESTINATION, not the next hex.** Decompiled the move: `DoMoveAlongTiles`
  calls `FlipPawnsGrid(next tile)` once, but the pawns then ride `MoveAlongPoints(smoothPoints)` — a smoothed curve
  through the tile centres — and their facing is that curve's tangent, which for "north on a hex grid" (NE then NW)
  points at the end tile from frame one. Fix: at arm time the pivot latches the bearing to the nearest moving unit's
  `PresentationUnit.NextWorldPositionOnPath` (`TryNextTileBearing`: army walk + the strike aim's `ToVector3` binding;
  re-asked for the first 0.5 s since the arm frame can precede the move loop's publish), eases to THAT, then
  `pivotStepDone` releases the park and hands the yaw back to the game's curve. `NextWorldPositionOnPath` added to
  the `PresentationUnit` catalog entry (the A7 coverage gate caught the bare read; bindcheck 130/130).
  **Fourth finding — "still sliding sideways".** The catch-up closed the gap along a straight CHORD to the live
  position; on a zig-zag that chord runs north while the tank faces the NE leg. Now the hold RECORDS the game's
  position + yaw every frame (`TurnState.trail/trailYaw`) and the catch-up REPLAYS that polyline at 1.5x (≥ 2 u/s),
  drawing the cursor and easing the yaw toward the heading the game had at that point — the rendered unit rolls the
  real legs facing along them. `[Pivot] arm / hex-step bearing / caught up / released` log lines make the next drill
  readable from the log alone.
  **Fifth finding — "it fights itself" (user's Custom 1° link).** Two arm rules were wrong: a re-arm was allowed
  mid-replay (meant for a second big turn), so with a 1° threshold every difference between the replayed yaw and the
  live yaw re-parked the unit every 0.5 s and yanked it toward a fresh hex-step bearing; and a pivot could arm while
  already rolling, where the smoothed curve bends the heading a degree or two per frame. Now a pivot arms ONLY from
  standstill (`TurnState.lastMovedT`, no movement for 0.25 s — read before this frame's own first step counts) and
  never while one is live; after the turn the vanilla movement owns the unit, the replay merely carries the rendered
  unit along the vanilla path to the live position. User's design: "only turn to the next grid cell, then let the
  vanilla movement take over."
  **Sixth finding — it turned toward the destination on the first leg.** The replay reproduced the game's recorded
  headings, and the game's smoothed curve bends toward the end tile from frame one — so a tank that had just lined
  up on the next hex immediately started crabbing. Replaced the trail replay with the user's THREE PHASES
  (`TurnState.pivotPhase`): 0 TURN in place to the next-tile bearing; 1 DRIVE straight to that tile (`pivotStepPos` =
  tile + the pawn's own formation offset, `StepPosFor`) with the heading LOCKED; 2 REJOIN — ease to the live position
  at 1.5x while the yaw eases to the live heading, vanilla owns it, release on arrival. `TryNextTileBearing` now also
  returns the tile and the unit centre. Log: `arm → faced the hex step → at first hex, vanilla takes over → rejoined`.
  **Seventh finding — the log settled it: "still sliding like before".** `[Pivot] arm: turn 150 deg at 90 deg/s` →
  `faced the hex step after 1.59 s` → `at first hex after 2.62 s` → `released by failsafe after 4.02 s, lag 2.7 u`.
  The real unit had a 2.6 s head start; no catch-up closes 4 u naturally. **Every position-faking approach is now in
  the graveyard.** Replacement: DELAY THE GAME. Decompiled the move start — the pawn's own Update re-calls
  `PresentationPawn.StartMoveAlongTilesIfPossible` every frame while a tile move is queued, so `Hk_PivotMoveHold`
  (`Patches/PivotHoldPatch.cs`, prefix → `ShouldHoldMoveStart`) answers "not yet" for `turn ÷ rate + 0.1 s` (cap 4 s):
  the unit turns standing still (FlipPawnsGrid already stamped the new facing; the ease swings to it), then the
  vanilla smoothed path starts from rest, untouched. Hold keyed PER UNIT (`moveHoldByUnit`, decided by the turn state
  at the unit centre) so the crew waits with its gun. `TurnState.pivotDeg` is published by `ApplyTurnEaseCore` so the
  hold reads the threshold off the same state as the rate (one source of truth, per the 08-05 lesson). Deleted:
  pivot phases, trail, `ApplyPivotFollow`, `TryNextTileBearing`, `StepPosFor`; `NextWorldPositionOnPath` dropped from
  the catalog, `StartMoveAlongTilesIfPossible` added (bindcheck OK). First drill of it: "no delay at all" — the log had
  no `[Pivot] hooked …` line: this plugin patches from an EXPLICIT `hooks` list in `Plugin.cs` (so one missing game
  member disables one hook, not PatchAll), and a new `[HarmonyPatch]` class is inert until it is listed there. Listed.
  **Drilled: "much better, almost perfect, no more sliding"** — one leftover: a one-second wait at the first hex. The
  log: a SECOND `holding move start 0.77 s: turn 60 deg` — the game hands the pawn its path in chunks and each
  chunk's start passes the same seam. Gate: `TurnState.lastMovedT` (set by `ApplyTurnEaseCore` on any position
  delta); `ShouldHoldMoveStart` refuses a unit that moved within the last 0.3 s — pivots happen from standstill only,
  a rolling unit bends onto the next leg the vanilla way. **Still paused** ("turns, moves, turns again, pauses, moves
  again"): the pawn genuinely STANDS at the chunk boundary for longer than 0.3 s, so the boundary counted as standstill.
  Gate raised to a REAL stop (≥ 1 s), a released hold now persists until the unit has actually moved (the re-issued
  chunk on release can't re-hold), and every chunk start that would have qualified logs `chunk start passed through:
  stood X s, turn N deg` plus the hold line carries `stood`/`pawn-unit gap` — the next drill is measurable.
  **Measured: `stood 1.5 s, pawn-unit gap 0.0 u`** — the pawn AND the unit holder genuinely stopped 1.5 s at the
  intermediate tile before any second hold. The pawn-level seam was the wrong LAYER: holding only the pawns put them
  1.8 s behind the holder, the army could no longer extend the running path (`CanModifyPawnsPath`), the first chunk ran
  to Finalize (final-angle turn = "turns again"), and the next chunk started from rest. Moved the hold UP to
  `PresentationArmy.UpdateWaitForReadyToMove` (called every frame from OnUpdate; it issues `DoMoveAlongTiles` when the
  unit is idle or `ChangeMoveTiles` to extend): `ShouldHoldArmyMove` defers the WHOLE presentation move while the unit
  is idle (`MoveAlongTilesState == None`), from a real stop (1 s), for `turn ÷ rate + 0.1 s`; the facing during the
  hold is an aim override at the unit centre with the bearing between `positionHistory` tiles `idx` and `idx+1`
  (`AStarResults.Steps[].TileIndex` → `ToVector3`) — the hex direction FlipPawnsGrid stamps on release. The history keeps
  growing during the hold, so the released move gets the longer path at once. Catalog: PresentationArmy +4, PresentationUnit
  `MoveAlongTilesState`, new `AStarResults`/`AstarStep` types (bindcheck 132/132). **Verified in-game 2026-08-22:
  "Yes, finally, now it is moving perfect!"** — nine drills from the first cut to the one that ships.
  **Follow-up — the howitzer folded WHILE rolling** (its state-driven pre-move clip keys off the first position
  delta, which now comes after the turn). `IsMoveHeld(unit)`: both state polls count a held unit as moving from the
  arm frame, so the fold plays during the turn; `ShouldHoldArmyMove` stretches the hold to ≥ the pre-move clip
  (cap 8 s); the hold entry lingers 0.5 s past release so the poll never sees held→still→moving (which would have
  flashed the AFTER unfold). **Verified** ("yes, it looks good"). Then generalised on request — "any time a unit
  plays a deploy animation": `ShouldHoldArmyMove` now has TWO independent reasons to hold, the longer wins — TURN
  (eased + pivoting + >= threshold to the next tile) and FOLD (state-driven with a pre-move clip: EVERY move start
  from a real stop waits for the clip, turn or not; a model with no turn state uses the state poll's moving flag
  for the stop test). Verified in-game 2026-08-22 ("it looks good").

- **GLBCONV SPLIT-BRAIN — a verified fix silently regressed out of the deployed exe (2026-08-17).** A verified
  critical review found glbconv had TWO sources of truth that had each grown a fix the other lacked: ENCReload's
  `Program.cs.src` (Jul 12) alone held the **T5 mirrored-winding fix** (`GetDeterminant() < 0` → swap B/C so
  scale-(-1,1,1) vehicle halves wind outward), while this repo's `baker/glbconv/Program.cs` alone held the
  **multi-tile UV warning** (critical-review #6). The 2026-08-16 exe rebuild (ENCReload d6017cb) was made from the
  baker copy — so the deployed converter shipped with **T5 regressed**: mirrored halves of symmetric vehicles would
  render inside-out again. No gate caught it because nothing compared the two sources. Fix: T5 merged into
  `baker/glbconv/Program.cs` verbatim; rebuilt against the same committed `SharpGLTF.Core.dll`; **A/B-verified** —
  byte-identical OBJs on 4 FactorySource models (no mirrored nodes → no side effects) and a synthetic
  two-node mirrored .gltf proving the deployed exe kept inward winding (`f 4 5 6`) where the merged build swaps
  B/C on exactly the mirrored node (`f 4 6 5`); redeployed to `ENCReload/Tools/glbconv/`. **Structural fix:
  `Program.cs.src` deleted — `baker/glbconv/` in this repo is now the ONLY source** (BUILD.md rewritten to say so,
  with the A/B-verify-before-deploy procedure). Lesson for the record: every cross-repo file copy without a sync
  guard eventually ships a regression; this was the one that did. **Same-day follow-up:** the stale `baker/`
  Blender-script copies (`rig_anim.py` / `vehicle_rig.py` / `deploy_convert.py` + all of `baker/Tools/` — labelled
  "live", never executed by the pipeline, weeks behind `ENCReload/Tools/`) were **deleted** — same disease, same
  cure: one home per file. Verified end-to-end same day: Bake Smoke Test 5/5 (both static paths through the new
  exe), F8 in-game smoke PASS (0 injection errors), tank + Cobra visuals clean. **CLI hardening (same day):**
  a usage error and a non-numeric grid arg now exit 2 with a named error (the old `void Main` returned exit 0
  on bad usage — "success" to any caller); rebuilt, A/B-verified byte-identical on 3 models + the mirrored-node
  winding probe, redeployed.

- **SAVE-RELOAD ISOLATION — the organ-gun load-order bug (2026-08-16).** Loading a heavy save then another in
  one app run tore an animated custom unit (the organ gun) and, once the mesh bound, painted it the wrong donor
  **red**; a fresh load was always clean. The F8 GPU-mesh-buffer readout (a `+1 mesh / +4.7k verts` diff on the
  second load) ruled out buffer overflow and pointed at stale isolation, and a per-load registration dump found
  the cause: **`AnimationManager.AnimationLoad` fires once per PROCESS, not per save-load** — the whole
  model-axis re-arm hung off it, so a second session never re-registered our skeletons into the game's rebuilt
  `AnimationManager`. Fix: re-arm on the seams that *do* fire per session — **`PawnManager.Load`** (the universal
  one: save-load, reload, *and* a New Game) plus `Sandbox.Load` (so the district axis resets synchronously) —
  all via a thread-safe flag consumed on the main-thread `Update` (the hooks may be off it). A `[SessionProbe]`
  proved the whole thing: `AnimationLoad` fired only once even across a main-menu trip, and a New Game after a
  load re-registered (fresh skel ids) only once `PawnManager.Load` was wired in. That exposed a second, older
  trap: the re-arm cleanup `Destroy()`'d `e.tex` unconditionally,
  but for a normally-textured model that is the **shared bundle atlas** from `AssetDatabase.LoadAsset` —
  destroying it made the reload's `LoadAsset` return `null` (the red skin). Fix: `ModelEntry.texOwned` — only
  destroy textures the plugin creates, never a `LoadAsset`'d asset. Both verified in-game on the load-order
  repro. Bonus: the model-axis session cleanup (audio/deploy/state maps) now runs on *every* reload, not just
  the first.

- **128-vs-256 BONE WALL — doc reconciliation + a cold case closed (2026-08-16).** A documentation critical
  review found the bone-limit stated as *both* "256" and "128" across three docs. The **128-bone-INDEX wall is
  correct** (per-vertex bone indices break past 127; T-62-proven, deploy code uses a 124-bone wall) — the "256"
  figure is stale. Reconciled `Animation-Pitfalls`, `Factory-Manual`, and `Animated-Models` to 128 (index 127),
  the mech's count to **222**, and the fit mechanism to the deploy path's **pair-merge to ≤126**. This
  retroactively **closed the 26-day "mech wings UNSOLVED" cold case**: rig_anim had slimmed the mech to 222
  bones (under 256) and the wings *persisted*, which was read as "256 disproven, cause unknown" — but 222 is
  still over the real **128** wall, so bones 128–222 (the arm chains = the "wings") were always going to
  collapse. No engine-import decompile needed; the culprit was the GPU skin's per-vertex bone-index ceiling.

- **SHARED SCHEMA — 64 duplicated fields de-duplicated into one library (2026-08-16, verified end-to-end).** The
  `ModelDef` (editor, 128 fields) / `ModelEntry` (plugin, 148) god-object stored ~66 behavioral/sound/prop/tint/transform fields (incl. pawnDescription + the position Vector3)
  IDENTICALLY, hand-synced across two repos + two parse paths (the drift the schema-parity guard exists for). Those 64
  now live once in a shared netstandard2.0 `Haf.Schema.HafModelSchema` that both classes **inherit** — so the field
  can't drift, and (because they inherit) the hundreds of `e.<field>` hot-path uses + object-initializers didn't change.
  A POC first proved the mechanism (Newtonsoft + Unity `JsonUtility` both serialize inherited-from-DLL fields); then it
  was executed and **verified end-to-end**: plugin builds + 59 tests + loads in-game with the new `Haf.Schema.dll`
  dependency + injects all 22 units unchanged; the editor compiles and a Save round-trips all 66 fields (0 wiped).
  `tools/deploy-plugin.sh` ships both DLLs (a redeploy can't drop the dependency). **Deliberately partial:** the GUID
  fields are stored in different shapes (`int[]` vs `sa/sb/..`, a runtime choice) so they stay divergent under the
  parity guard — the worth-it slice, not a forced full merge. See docs/Shared-Schema.md.

- **HEADLESS BINDING DRIFT CHECK — reflection-drift net, step 3 (2026-08-16).** The in-game `haf_bindings_report.txt`
  still needed a launch to read. `bindcheck` (a net8 tool, `Tools/bindcheck/`, using `MetadataLoadContext`) now validates
  the whole `GameBinding` catalog against a Humankind build's assemblies **without launching the game** — it reads
  `Patches/GameBinding.cs` directly (always in sync, no manifest to stale) and inspects the game DLLs reflection-only, so
  Unity's native deps and static ctors are irrelevant. `Tools/check-bindings.sh [<Managed>]` builds it once and runs it;
  a game patch's binding breakage is now named **headlessly** (CI-able on a version bump) instead of found by launching.
  Separate trigger from the pre-push gate on purpose: that guards HAF *code* changes, this guards *game* changes.
  **Verified both ways:** `49/49` clean on the pinned `1.30` build, and it correctly flags an injected fake binding
  (exit 1). Closes the maintainability review's #3 (binding half).

- **DECISIONS (ADR) LOG + backlog triage — shrinking the bus factor (2026-08-16).** The maintainability review's #4.
  Added [`docs/Decisions.md`](docs/Decisions.md) — short records of the *settled* decisions and the *why* behind them
  (pack order follows HK's mod order & why the base-flag was rejected; make-drift-loud over removing reflection; the
  Factory/Lab ownership split; the declined `ModelEntry` POCO split; pair-merge vs slimming for >127-bone rigs;
  rotation-only animation; framework-neutral naming; first-loaded-wins conflicts; the focused-test stance) — so the
  tribal knowledge that would otherwise be reverse-engineered from the code has one home, linked from the docs index +
  `llms.txt`. Also triaged the backlog: recorded the reflection-fragility A5 progress against the GameBinding-gaps item
  (narrowing it to the off-catalog district types + the struct-typed surface), and gave the `rotorSpin` item an honest
  status (parity now allowlists it, but the Save-wipe of hand-authored runtime-only keys is the real open concern).

- **ONE PRE-PUSH GATE — the fast guards are now un-forgettable (2026-08-16).** The maintainability review's #2: the good
  guards (`dotnet build`, `dotnet test` ×59, the Roslyn editor compile-check, the 4-path registry schema-parity) existed
  but ran manually, one at a time, across two repos, with no enforcement. Now one **`Tools/check.sh`** per repo runs its
  fast guards and prints an aggregate PASS/FAIL, wired as a version-controlled **pre-push hook** (`git config
  core.hooksPath Tools/git-hooks`) so a broken build / failing test / drifted schema can't be pushed. Standing it up
  **immediately caught three latent schema drifts** (exactly the "forgotten check" problem): a wrapper field the plugin
  read but the baker never wrote (`module`/`moduleGuid` — added to `RegistryFile`), two runtime-only keys the guard should
  allowlist (`rotorSpinBones`/`rotorSpinSpeed`), and a `float?` read-cast the parity script mis-classified as a type
  mismatch (its nullable handler covered `bool?`/`int?` but not `float?`) — all fixed to green. Heavy guards
  (deploy golden-master, in-editor Feature Test, the in-game binding report) stay out of the sub-minute gate.

- **MACHINE-READABLE BINDING REPORT — reflection-drift net, step 1 (2026-08-16, verified in-game).** The maintainability
  review flagged game-update fragility as the top structural risk: ~1,475 reflection bindings that fail at *runtime*, found
  by squinting at the log. `GameBinding` already cataloged ~47 game types + their members and validated them at startup
  (A1), but only logged + fed F8. Now `ValidateAndLog` also writes **`BepInEx/config/haf_bindings_report.txt`** every
  launch — game version, verified version, `resolved N/N`, then one `[ok]` / `[MISSING TYPE]` / `[MISSING MEMBER]` line per
  binding — a diffable file (next to `haf_load_report.txt`) that a game patch, or a headless CI launch on a new build,
  turns into one report naming exactly what broke. Also migrated the **first raw-reflection site** onto the catalog as the
  pattern for the rest: `GetRuntimeModules()` (pack order) now resolves via `GameBinding.FrameworkServices` /
  `RuntimeService` instead of a raw `Type.GetType`, and both are in the Catalog (47 → 49). **Verified in-game:**
  `resolved=49/49  missing_types=0  missing_members=0`, both new bindings `[ok]` (no late-loader false positive).
  **Coverage batch 1 (same day):** an evidenced audit of the load-bearing injection path added ~60 reflected members to
  the Catalog (49 → ~124) — `AnimationManager` gained `AnimationLoad`/`RegisterMeshCollection`/`GetPoseTRS` + the
  `gpu*Buffer` fields (re-arm + pose), `PawnManager` its descriptor buffers + `pawnEntries`, the empty `ContentLayer` its
  mesh-buffer + compute-buffer members, and the district Element/Selector/District their level-build members. The report
  validated the lot on 1.30 in one launch (`missing_members=0`) — the self-correcting property: a mis-attribution would
  have surfaced as `[MISSING MEMBER]` on the known-good build.

- **PACK ORDER FOLLOWS HUMANKIND'S MOD ORDER (2026-08-16, verified in-game).** A HAF pack is the content-extension
  of a Humankind runtime module, so packs should load in the SAME order the game loaded their modules — the player's
  own mod order — not an invented alphabetical/base rule. This also retired a dead guarantee: the loader still claimed
  "the base registry loads first, so ENC is protected," but ENC left the `haf_models.json` base slot for
  `haf_packs/ENCReload/pack.json` long ago, so that protection had silently lapsed (zero impact today at one pack, but
  wrong the moment a second pack sorted before `ENCReload`). Fix: read the game's ordered active-module list via
  `Amplitude.Framework.Services.GetService(Amplitude.Mercury.Runtime.IRuntimeService).GetRuntimeModules()` (a `string[]`
  of `Name\GUID\…` in load order; fully reflected + guarded), match each pack to its module (by `moduleGuid`, else
  `module`, else the pack's **folder/file name == the module Name** by convention — computed independently of `modId`,
  since ENC's `modId` is `enc` but its folder/module is `ENCReload`), and sort packs by the module's load-order index.
  `dependsOn`/`loadAfter` still layer on top; an unmatched pack or an unreachable API falls back to alphabetical. No
  pack.json or editor change — ENC maps automatically via its folder. **Verified in-game:** `haf_load_report.txt` reads
  `HK module order: enc #1→ENCReload` (matched its module at load-order index 1, right after vanilla). Critical-review #7.

- **MODEL FACTORY UI clarity + compaction pass (2026-08-16, editor).** Renamed two implementation-leaky labels
  to what the modder actually gets — **"Convert grid" → "Weld & simplify (0 = keep exact)"** (it's glbconv's
  vertex-weld resolution, not a grid; tooltip now points a textured model at "Reduce to ~tris") and
  **"Height-based UVs" → "Height-gradient UVs (untextured)"**; shortened **"Re-spawn after load (borrowed rotor
  fix)" → "Respawn after load"** (the rotor-fix detail stays in the tooltip). Display labels only — the fields and
  registry keys are unchanged, so every existing `pack.json` keeps working. Also compacted the layout: the two
  geometry-reduction knobs share one row; the three shading toggles and the four runtime donor toggles each
  collapse to a single right-aligned row; and both transform vectors render label + X/Y/Z on one line (a custom
  `EditorWindow` defaults `EditorGUIUtility.wideMode` to false, which had wrapped them). Docs synced.

- **glbconv warns on multi-tile / UDIM UVs (2026-08-16, tool).** The OBJ tile-shift normalizes UVs by a single
  integer offset (`floor(min U/V)`), which only rescues a ONE-tile island (the Zeppelin envelope in V 1..2). A
  model that tiles across >1 UV tile (a `.1001-.1005` UDIM camo set) left the other tiles outside [0,1]; the
  single-tile atlas can't wrap them, so part of the skin sampled outside the rect and **silently vanished**. Full
  UDIM consumption stays a deliberately-deferred feature (manual Blender texture-transfer workaround), so this
  doesn't add it — it makes the failure LOUD: a stderr `WARNING` (glbconv stderr surfaces as a Unity warning) with
  the U/V spans + the fix, emitted only when the shifted UVs still reach past one tile. Verified: no false-positive
  on a real single-tile multi-material model; fires on a synthesized 2-tile GLB (`U 0..2`). Critical-review #6
  (source `baker/glbconv/Program.cs`; rebuilt exe deployed to ENCReload `d6017cb`).

- **STATIC bake re-extracts on a changed input (2026-08-16, editor).** The static path's extraction gate skipped
  the whole prep+convert block whenever the OBJ merely existed and 'Reuse extracted' was on (`!reuseExtracted ||
  !haveObj`), so changing the source file, the converter, or a convert arg (grid / strip / reduce / double-sided —
  all of which shape the OBJ) was silently ignored and a stale OBJ re-baked (the "rotation doesn't respond" trap).
  The ANIMATED path already guarded this; the static path didn't. Fix (ENCReload `e85e6c5`): mirror the animated
  path's three busters — `glbconv`/`prep_model.py` mtime, source-file mtime, and a settings fingerprint in a
  `<name>.extract.args.txt` sidecar. No-op when nothing changed. Critical-review #5; editor-verified in-Factory on
  StealthCruiser (tool-newer + args-changed busters both fired on a grid change).

- **MODEL FACTORY rename is a real rename now (2026-08-16, editor).** Editing the Resource-name field of a
  loaded entry and then Save / Save-settings / Bake keyed the ownership rebase + GUID-carry on the *new* name,
  which matched nothing — the rebase early-returned, the carry was skipped, and `Upsert` **added a second entry**
  while the old one and its baked assets orphaned. A rename silently made a duplicate. Fix (ENCReload `170e329`):
  resolve the source by the name the form was LOADED under (`existing[selected]` — the same reliable signal the
  Remove button keys on; null/`<New>` for a fresh or cloned form, so a Clone is never a rename). The rebase +
  carry key on that, so the renamed entry inherits the source's Lab-owned fields + baked GUIDs (Unity GUIDs are
  filename-independent → a no-bake rename resolves in-game with no re-bake); the old entry is dropped after a
  successful Upsert, and a rename onto a name a DIFFERENT model owns is refused rather than clobbering it. Also
  collapses the case-only-rename twin-entry case into one entry. Editor-verified with a `SiegeHowitzersCar` ↔
  `SiegeHowitzersCar2` round-trip: one entry each way, `git diff` of the registry was a single renamed line.

- **DISTRICT selectorGuid guard (2026-08-16, editor).** A re-bake minted a fresh `fxMesh` (delete+create) but
  only *set* `selectorGuid` on selector-bake success and never cleared a stale one, so a selector failure left
  the district Upserting as "Baked ✓" while routing through the scoped path with an old selector against the
  new mesh — a broken district reporting success. Fix (ENCReload `9584b23`): clear `selectorGuid` before the
  (re-)bake so a failure genuinely falls to the legacy path, as the code already promised.

- **DISTRICT CLONE LEAK — critical-review follow-up (2026-08-16).** A full-framework critical review (plugin +
  editor) surfaced that the district axis had the *same* leak class just fixed on the model axis:
  `ResetDistrictSessionState` only **nulled** its runtime `Object.Instantiate` clones (private leaves, cloned
  selectors/output-layers, deep-clone material nodes, the B&W gray albedo), which Unity's unused-asset sweep
  never collects — so every in-session reload leaked a native FxOutputLayer + N cloned FxEvolverMaterials + a
  gray texture per scoped district. Fixed with explicit ownership tracking (never touching `LoadAsset`'d bundle
  assets) and a main-thread destroy queue (the reset runs off-thread via `Sandbox.Load`). In-game verified across
  reloads (`[District] freed N runtime clone(s)`, no district errors).

- **`hideSubPawns` COEXISTENCE — critical-review follow-up (2026-08-16).** The gunship duplicate-pawn hide (keeps
  one pawn, buries the stacked squadron copies) counted per model *type*, not per unit — so a second coexisting
  unit of the same model (yours + an enemy's, or two of yours) rendered **nothing**. Fixed by keying "already
  kept this frame" on unit *position* (a unit's stack shares a spot; a different unit is tiles away). Verified
  in-game with several gunship helicopters on screen at once, each a single clean model.

- **UNIT→ENTRY MATCH UNIFIED — critical-review follow-up (2026-08-16).** Repoint resolved a unit to its entry by
  longest-match on the full `pawnDescription`, but the movement/deploy/state polls used *first-in-registry*
  substring on `coreDesc` (the `_NN`-stripped stem) — so two entries sharing a stem (`Foo_01`/`Foo_02` = distinct
  models) repointed to distinct models but animated/deployed/sounded from whichever sorted first. Fixed by routing
  every per-unit path through one matcher (`FindEntryForUnitDefinition`) that tries the full `pawnDescription`
  first (distinguishes `_01`/`_02`) and falls back to `coreDesc` (never regresses a working bind). Latent for the
  reference pack (no stem collisions) but a real correctness gap for third-party packs.

- **FACING SURVIVES RESPAWN (2026-08-16).** `respawnAfterLoad` units (the helicopters) lost their saved heading on
  load: the ~3-frame post-load `UpdatePawns` rebuild recomputes `FormationAngle` to neutral *after* the single-shot
  facing-restore already fired and closed (non-respawn units like the organ gun kept theirs). Fixed by coordinating
  the two systems — `MaybeRespawnPostLoad` re-arms `FacingPersist` right after each respawn, which re-applies the
  saved angle once the rebuilt unit is loaded + stationary (same frame, no neutral flash; still skips units the
  player is moving, so no crab-walk). Verified: a helicopter saved facing east holds its heading across a reload
  (`[Facing] re-applied army … after respawn`).

- **FORMATION PURE-REPOINT REFORM — critical-review follow-up (2026-08-16).** The catch-up that re-instantiates
  units which spawned *before* a formation override landed only fired for entries carrying dummy data — a
  **pure-repoint link** (points a unit at a formation already in the DB, no authored dummies) was excluded by the
  `dummies.Count > 0` gate, and its "already full?" test compared against `e.dummies.Count` (= 0), so its
  pre-override units kept the old pawn count until a reload. Fixed with a new `Entry.targetCount` (the target
  formation's real `Dummies.Length`), computed in `ApplyOne`, and by including unit links in the reform selector.
  Zero change for ENC (all its entries carry dummy data → same inject/overwrite path); protects third-party packs
  that repoint a unit to a vanilla formation.

- **GAMEBINDING COVERAGE — the army-walk root (2026-08-16).** Critical-review finding #5: the `Presentation`
  Dep was catalogued with *zero members*, so `PresentationEntityFactoryController` — the static army-walk root
  that respawn, facing-persistence, class-scan and the descriptor census all read — wasn't validated. A game
  rename there would silently no-op all four with nothing in the health report. Added it (plus the factory's
  `PresentationArmyEntities` next hop): catalog 46 → 47 types, report clean (`OK — 47 game type(s)`). The
  fragility-plan "make drift loud" template applied to exactly the code the recent respawn/facing fixes touch.

- **AUDIO DEATH/BATTLE GATE (2026-08-16).** Critical-review finding #6: the `_audioOn` poll gate omitted
  `soundDeathFile`/`soundBattleFile` while the loader right below it (and `OnPawnDeath`/`ProcessBattleCries`)
  consume them — so an entry with *only* a death rattle or *only* a battle cry never entered the poll, its clip
  never loaded (silent death cue), and `ProcessBattleCries` re-enqueued the cry every frame forever. One-line fix:
  add both fields to the gate, mirroring the loader's own check. Zero change for ENC (no death/battle entries);
  protective for its built-but-unshipped creature voices and third-party packs.

- **STATE-MACHINE GATE (2026-08-16).** Critical-review #8: `StatePose`/`ProcessAnimStates` ran only when
  `moveAnimId >= 0`, but attacks armed on `attackAnimId >= 0` — so a move-less state-driven model (idle+attack,
  no move clip) armed fires that never animated. Fixed with a shared `ModelEntry.AnyStateRole` predicate driving
  all three gates, plus a guard on the `moving` pose branch so a move-less model that moves falls back to idle.
  Zero change for ENC (all its state-driven models have a move clip); protective for a stationary-turret-style unit.

- **RUNTIME-CLONE LEAKS — critical-review #7 (2026-08-16).** Three more leaks in the district-clone family:
  (1) `InjectHandProp` overwrote `e.handPropLayer` with a fresh `Instantiate` clone on every re-inject (LOD /
  save-load / respawn drops the prop fragment), orphaning the previous native FxOutputLayer — now the old clone is
  Destroyed first (affects ENC's hand-prop units, no visible change). (2) `BuildAdjustedAtlas` and (3) `MakeGrayCopy`
  (the B&W footprint) returned `null` on a `ReadPixels`/`Apply` throw without releasing the pooled RenderTexture,
  restoring `RenderTexture.active`, or freeing the half-built texture — and `TickOne` retries every frame. Both now
  use try/finally so the RT + active are always cleaned up and the partial texture is freed on failure. Normal
  rendering unchanged; verified no-regression in-game.

- **BAKE-SCRIPT SILENT-MIS-BAKE GUARDS — critical-review Tier 3 (2026-08-16, in the ENCReload `Tools/`).** Three
  bake foot-guns that shipped a broken rig with **exit 0** are now loud aborts: (4A) `rig_anim.py` printed the
  rest-fold frame-0 residual (`should be ~0`) but never asserted it — a fold that completes yet leaves a bone
  displaced (the "head off shoulders" class) shipped silently; now aborts on NaN or a residual > 25% of the rig's
  bone scale. (2A) a failed `transform_apply(rotation+scale)` on the conversion path was swallowed with a warning,
  shipping a skeleton ~100× off the mesh; now hard-fails. (4B) `deploy_convert.py` with zero animated parts built a
  StaticRoot-only rig and shipped a static single-bone model; now aborts. Verified: the OrganGun re-baked clean
  (residual `0.000000` asserted OK, rotation+scale applied, `ANIMATED DONE`, no false abort).

- **MODEL FACTORY — Remove flow fixed (2026-08-16, ENCReload editor).** Critical-review #1 + a follow-on: (a) the
  Remove button reset `selected` but not `sel`, so the popup-apply reloaded the stale index on the shrunken list —
  jumping to a different entry, or `IndexOutOfRangeException` when the removed entry was the alphabetically last
  (Clone already reset both). (b) The "delete baked assets?" prompt was a second sequential modal that could be
  missed, and once the entry was gone it could never be re-triggered (orphan assets, no cleanup). Both replaced by
  one `DisplayDialogComplex` on Remove — **Remove + delete files / Cancel / Remove, keep files** — so the delete
  question is always asked once, reliably; deletion still uses the exact `OutputSuffixes` whitelist (never a glob).

- **BATTLE GUNNERY — the Jagdpanzer arc (2026-08-06).** A casemate tank destroyer exposed, one shot at a
  time, that vanilla **never rotates a vehicle's hull in battle** (vehicles aim only via a turret bone slot —
  invalid on custom rigs), and grew the full gunnery chain in a day: **battle hull-aim** (the map bombard's
  aim machinery armed per volley — the eased hull lays on the actual target, `hold=1` waits for the lay);
  the **gun-vs-turret model** in the Animation Lab (a Turret bone *yaws* and classifies the vehicle turreted;
  a Gun bone aims with the hull and only *elevates*); **distance-proportional gun elevation** (user spec:
  raised by range to a configurable max, rising while the hull turns, lowered after the shot); the **muzzle
  dial gone gun-local** (rotates with aim + elevation, now moves flash, tracer AND smoke — a world-space dial
  can't follow a turning hull, and a bone's TRS sits at the breech, not the barrel end); and **post-shot
  facing that settles on the nearest clean facing toward the shot** (v1's yield-on-yaw-change heuristic
  couldn't tell a real order from the choreography's own post-fight reset — graveyarded). Every asset in the
  chain is the game's own; HAF only fixes where and when. See [docs/Turn-Ease.md](docs/Turn-Ease.md).
- **CATEGORY TURN EASE — every unit type turns in character (2026-08-06).** Turn ease graduated from a
  per-model knob to a **game-wide system with per-TYPE defaults** — human / land / turret / hover / ship —
  each classified by **characteristic, never by name** (user rule, enforced twice): capability profiles, the
  game's own `Hover` "ignores terrain" ability, and live azimuth-transform detection for turrets; fixed-wing
  planes are excluded outright (they already fly natural curves — user call). Hover and ship carry their own
  bank (`hoverbank`/`shipbank`: a chopper banks, a ship heels), and precedence flipped to per-model > per-unit
  link > category > global. Getting the *strike hold* to follow the category cost four measured bugs, each a
  different naming-layer trap (an entry dead-end, artillery rendering its LIMBERED variant whose name extends
  the unit's, the servant CREW answering for its gun, and artillery main-gun pawn definitions that never pass
  the addon hook at all) — closed structurally: the slow class scan reads the rendered unit itself and is the
  classification authority, and the hold reads the eased pawn's ground-truth rate off its live turn state, so
  the visible turn and the fire hold cannot disagree by construction. Configured from the Formation Override
  window's **Turn ease defaults** panel (live dial write). See [docs/Turn-Ease.md](docs/Turn-Ease.md).
- **ATTACK TURN — the howitzer pivots, THEN fires (2026-08-05).** A map bombard used to teleport-snap the
  unit's facing and fire in the same instant; now a HAF model with a turn-ease rate **sweeps to the attack
  heading first**, and every observable of the shot — muzzle flash, shot sound, shell, impact, the model's own
  recoil clip — **waits for the barrel** and lands together at alignment. Six iterations to find the real seam,
  each killed by a measurement: the battle choreography (LookAt actions, rotation FSM) is a **no-op on the
  world map** (`StepTurning` runs 0→0 — the snap is `FlipPawnsGrid(Teleport)` stamping the GPU pawn data);
  patching the unanimated-rotation method silently did nothing because **the JIT inlines it** into its caller;
  and a one-shot delay at attack start **raced the snap** and computed zero. The fix rides HAF's own seams: the
  Comanche's ObjectSpace turn ease generalized to every entry, plus three holds keyed off the same
  remaining-turn time — the artillery controller's scheduled launch/hit delays, a deferred
  `TeleportToSimpleAttack` (the muzzle/sound carrier), and the fire clip's clock pinned until aligned.
  Turn ease also smooths ordinary move-order facing for any model with a rate. Same day, two extensions, both
  verified: **vanilla units** get the identical treatment through a Formation Lab link (per-unit rate, resolved
  to the pawn descriptor at load; a link on a Common unit covers its culture-emblematic variants — found when
  the player's ZULU siege howitzers ignored the Common link, by a one-line-per-descriptor render census), and
  **true-bearing aim** — the eased turn exposed that vanilla bombards face a HEX-QUANTIZED angle (one of six
  directions, up to 30° off); the ease target now becomes the real bearing to the target tile while the strike
  plays out, so the barrel lays exactly on the city it shells. The aim then surfaced **three more vanilla
  shortcuts** (2026-08-06, each spotted by the user frame-stepping captures, each verified fixed): the strike
  ran on TWO CLOCKS (dynamic release vs padded schedule — the bang drifted ~0.25 s from the recoil; now one
  shared release timestamp armed before the flip), the attack clip teleported in at a RANDOM PHASE while the
  shell was timed to its literal event time (now deterministic frame-0 playback), and the shell + muzzle smoke
  spawned at the PRE-PIVOT barrel — vanilla captures the muzzle at schedule time, and the pawn's invisible
  transform skeleton never turns with the eased model (now: fire-time recapture + every bone TRS aim-rotated at
  the GetBoneTRS seam while the strike is live). See [docs/Turn-Ease.md](docs/Turn-Ease.md).
- **CLIFF ANTICIPATION — climbing before the edge, not into it (2026-08-05).** Terrain hug's lead point now
  also reads the *ground* ahead: where the terrain steps up, the aircraft gains that height immediately instead
  of rising at the cell boundary, and the engine's own tile-bound altitude catches up on arrival (climb-only —
  anticipating a descent would sink toward the ridge still being crossed). Needed a physics reference and one
  correction found by reading the log rather than the screen: the first probe was a plain downward raycast and
  measured the helicopter's **own army collider**, so it compared unit heights, not terrain; it now uses
  `RaycastAll`, skips units, and takes the lowest hit. Dial: `cliff` in `haf_hugterrain.txt`.
- **TERRAIN HUG — nap-of-the-earth flight, climbing only for the city (2026-08-05).** The helicopter now
  **skims low over open ground and climbs only for built districts**, instead of cruising at skyline height
  everywhere. The engine's air altitude is already terrain-relative (it follows hills for free) but ignores
  buildings — so the model's `position.z` lift is now *subtracted* wherever no built district sits under or
  ahead of the unit, with the probe **leading** along the movement vector so it climbs before the buildings.
  Two measurements replaced two guesses: the map's **tile spacing is derived** from the median
  nearest-neighbour distance between districts (6.93 units on the test map → auto match radius 3.81 = "this
  district's own tile"; a hand-picked radius lifted the unit for every field beside the city), and districts
  are classified by their private **`constructibleDefinitionName`** rather than the always-identical
  GameObject name — which exposed that Humankind renders cultivated tiles as districts too (`Exploitation`,
  `Ruin` are flat; only `Extension_*` carries buildings). Live-tunable via `haf_hugterrain.txt`
  (drop/radius/lookahead/ease + `only`/`skip` name filters). See
  [docs/Donor-Clip-Flight.md](docs/Donor-Clip-Flight.md).
- **TURN EASE — flown turns instead of the facing snap (2026-08-04, same day as the flight milestone).** The
  engine snaps a pawn's facing instantly on a move order; the Comanche now **sweeps** to its new heading at a
  capped rate and **banks into the turn**, composed under the nose-down attitude machinery. Every angle eases
  (180s included) while teleports/battle placement snap naturally — the per-pawn state is position-matched, so
  a jumped pawn simply starts fresh at the target heading. Live-tunable in-game via `haf_turnease.txt`
  (rate/bank, ~1/s poll) — dialed to feel on the first flight. Spotted as a gap by **shakee** on the milestone
  video within minutes of posting; built and verified the same evening. Per-model Factory fields are the
  planned graduation. See [docs/Donor-Clip-Flight.md](docs/Donor-Clip-Flight.md).
- **DONOR-CLIP NATIVE FLIGHT — the donor's own animation on our rig (2026-08-04).** The Comanche now flies with
  the donor gunship's **complete original animation** — hover bob, main rotor flat on the mast, tail fan spinning
  in its own **canted** ring — driving OUR baked mesh natively (`useDonorClip`, now a Factory checkbox). Cracked
  with instruments, not guesses: a `[Rest]` skeleton dump (donor rigs keep ALL rests identity; ours carried the
  glTF -90°X on bone 0 and the facing rotation on Root — each **conjugates** every animated descendant, because
  the engine composes clips ON TOP of rests) and a `[DonorAxis]` decoder that read the donor channels straight
  from the GPU records (ch2 main = pure local-Y spin ~18°/frame; ch3 tail = pure local-X ~36°/frame). The fix is
  two-sided: the plugin **rebases the injected skeleton at registration** (ancestors → identity rests with world
  positions preserved — and it MUST run before `AnimationManager.Apply`, which snapshots BoneInfos into the GPU;
  leaf rotor bones keep their orientation), and the Vehicle Lab **authors the axle frames** (main-rotor bone
  local Y = mast, tail-fan bone local X = the canted fan axle). Five failure modes catalogued on the way
  (index-shifted channels, rolled axis, orbiting rotor, vertical loop, stale-rig rebake) — the full contract
  and catalog: [docs/Donor-Clip-Flight.md](docs/Donor-Clip-Flight.md). Plus a live `haf_rotortrim.txt` dial
  (constant BR-slot tilt, re-applied to live pawns ~1/s, no relaunch) kept inert as a finishing tool.
- **A HELICOPTER WITH ITS OWN SPINNING ROTORS — and the four-mechanism ghost hunt (2026-08-03/04).** The RAH-66
  Comanche now flies with **its own main + tail rotor spinning** (Vehicle Lab Rotor/Tail-rotor roles → continuous
  bake) instead of borrowing the donor gunship's. Getting there uncovered — and defeated — FOUR stacked mechanisms
  that together were the old "a donor's rotor can't be removed" wall: (1) gunship-class units spawn a **squadron
  of pawns** via the air hardcode (formation dummies don't cap them) → stacked copies of the model; fixed by
  keep-first-hide-rest (`hideSubPawns`); (2) our own `respawnAfterLoad` **leaked live sub-pawns** per attempt →
  off for own-rotor models; (3) one leaked pawn kept the **pre-injection donor cache** → the cached-struct repair
  (SRCFIX); (4) the last ghost — a translucent rotor that survived crushing every vertex of every ContentLayer,
  every pawn sweep, and a full renderer census — was **not geometry at all**: the donor's Mecanim-event **VFX
  billboard**, a 2D rotor sprite (the user's "it has no depth" observation cracked it), dropped by the July-era
  `silenceDonorVfx` flag. One registry flag; four hours of elimination to learn which one. The live **ghost-bisect
  tool** (file-driven in-session vertex surgery: crush/restore/census, no relaunches) ships from the hunt.
  Planned: a per-NAME VFX filter (`silenceDonorVfxNames`) to drop only the rotor sprite while keeping other donor
  effects, and a `moveTilt` nose-down attitude while moving (wired, dormant).
- **HAND PROPS — a weapon on a custom skeleton (2026-07-19).** The Combine soldier **carries a textured M60**,
  gripped correctly through idle, run, combat stance, and sustained fire. The donor (a vehicle) has no weapon
  slots, so the plugin constructs the pawn fragment itself and glues the Prop-Lab mesh to the injected skeleton's
  hand bone — with a surgical GPU-descriptor patch (the naive full rebuild scrambled other units), a per-tick
  repaint of the prop's own atlas on a private layer clone (Amplitude streams weapon textures and resets the
  material), and an always-stamped import-angle override (the baked angle field doesn't survive the mod bundle —
  the engine's `-90°X` class default silently tipped every prop until neutralized). Authoring: bake in the Prop
  Lab (now with per-prop saved recipes), pick it in the Animation Lab's **Hand prop** combobox, done.
- **STATE-DRIVEN characters — idle / run / after-move / combat stance / attack fire (2026-07-19).** The Combine
  soldier **idles standing, RUNS while moving, holds a weapon-raised COMBAT STANCE while its army is locked in a
  battle, and fires its ATTACK animation when it actually shoots** — five clips per model, switched live by the
  runtime (a ~20×/s state poll + per-pawn pose selection on the proven Pose0 slot; priority attack > move >
  after-move > combat > idle). The attack trigger is a hook on the game's own per-pawn ranged-fire sequence, so
  every battle volley animates the exact shooting pawn; an **Attack repeats** knob loops a short recoil-pop clip
  into sustained automatic fire (the soldier's 0.17s `shootAR2s` × 18 ≈ 3 s of fire, runtime-only — no re-bake).
  Configured entirely in the Animation Lab: a **State-driven** toggle with **Idle / Movement / After-movement /
  Attack / Combat-idle** clip pickers; all roles bake against **one shared skeleton** in a single Blender pass
  (every clip rebaked against the primary clip's frame-0 rest — per-role rests would displace the non-primary
  clips; single-frame stance clips are auto-padded so Unity's importer can't drop them). The bake-side war story:
  Blender's bone rename only syncs the *assigned* action's curve paths, so dormant role clips exported as frozen
  statues until the paths were patched explicitly — caught by byte-level pose-data analysis, fixed, and guarded by
  a tool-version cache-buster.
- **A HUMANOID character — a full 62-bone rigged soldier (2026-07-18/19).** The Combine soldier replaces a vehicle
  unit: right-sized, standing, head on his shoulders, **turning with its movement**, idling on his own baked clip —
  the first true *character* through the pipeline (props and machines came first). Getting him there built the
  **raw-rig conversion**: auto-rigged models whose clips *assemble the body from a scrambled rest via location keys*
  (which Amplitude, rotation-only, can never play) are now **rest-normalized and visually re-baked** at bake time —
  the assembled pose becomes the rest, the whole clip is re-derived as pure rotations (in-bake verified to ~1e-4),
  the export folds units/rotation/scale into the data, collapses no-op roots, and renames bones topologically
  (Amplitude sorts alphabetically and requires parents before children). A **litmus rig** (12-deep chain of cubes,
  `Tools/make_litmus.py`) proved the runtime renders clean rigs perfectly. Also discovered en route: the game turns
  pawns through a procedural **bone-rotation layer** — the plugin clears it only for artillery models and ignores
  vehicle donors' phantom wheel-spin slots. *(With the clean rig, the unit's fired-drone projectile also displays
  again during attacks — the fully working unit: stand, turn, idle, launch.)*
- **Animated custom models — a first, one-click.** A quadcopter drone injected onto a land unit renders full-size,
  textured, and **spins its own propellers from its own baked animation** — for any number of instances. Tick
  **Animated**, press Bake.
- **A wheeled vehicle with its OWN spinning wheels (2026-07-24).** The Ehrhardt armored car (Era5) replaces the
  Armoured Car — a purpose-made *skinned* rig whose **four wheels spin in place while it drives and are still when
  parked** (state-driven: Idle = a held frame, Movement = a spin slice). En route it pinned down a nasty engine
  trap: on the legacy path a **rotating** bone flings off in-game (idle fine, movement flings) because the
  metre→centimetre FBX export leaves a **×100 sandwich** Amplitude's TRS composition mangles — the same mechanism
  as the soldier's head. The fix is **Convert raw rig ON + Fix 100× OFF** (cancels the ×100 at export), which
  overturns the old "clean rigs skip convertRig" rule for any rig with a spinning part. It also grew a hands-free
  **Auto-ground (sit on terrain)** bake toggle — drops the tyres to the skeleton origin, self-correcting and
  **size-proof** (no manual height dial, stays grounded across Size changes). Extracted from an Unreal "Game
  Template" (Fab).
- **The rotation-only barrier is DEAD — true bone TRANSLATION plays in-game (2026-07-25/26).** Decompiling the
  runtime proved the clip format supports `RotationTranslation` (vanilla tank treads use it); the "rotation-only
  law" was our own bake's strip. The opt-in **`Keep bone translations`** flag carries authored slides through the
  bake — first verified on a sliding test bone, then shipped as **the M114 howitzer's REAL kickback**: fire, the
  tube slams back and glides home, barrel lowers, shell loads, aiming raise — the animator's complete cycle,
  finally rendered (multi-segment recoil windows with per-segment speed steps: `442..530,305..441/2`). En route,
  a decade-class root cause fell: a sentinel value placed a helper bone at 10⁹ units, collapsing bone chains via
  float32 cancellation — the origin of every NaN import warning this pipeline ever produced.
- **Moving caterpillar tracks — path-instanced rigid links (treadize, 2026-07-26).** Mark a tank's tread loop
  **C (Caterpillar)** (+ barrel **G**) in the Vehicle Lab and it becomes a real rolling track: the link pitch is
  measured off the cleats (autocorrelation), the loop path is built as the classic *belt around pulleys* from the
  wheel centers + measured band radii, the mesh is cut into **half-link cells at the cleat gaps — one bone each,
  no skin blending** — and every link rides the path with advance = exactly one link per loop (invisible restart,
  tread ≈ sprocket surface speed). Seventeen revisions of blended-skin approaches lost to the eye's verdict —
  "molded links bending = slack" — before rigid instancing won; the full post-mortem lives in
  [Animation-Pitfalls](docs/Animation-Pitfalls.md). Runs in-game (after a five-defect debugging chain); a remaining
  idle micro-twitch is the open polish item.
- **A turret that AIMS at the target (turretize, 2026-07-24).** The armored car's turret now yaws to track the
  enemy — by hijacking the game's OWN aim: the engine streams a heading angle into a `PawnEntry.BoneRotation` slot
  that lands on an invalid bone index for injected models, so we retarget that slot to the turret bone and the
  engine's aim math drives it (no per-frame trig). Runtime-only (**Turret bone** + **Turret aim axis** in the
  Animation Lab, Save + relaunch). The aim axis is per-model — yaw for a turret, and the *same* knob gives **pitch**
  for a future mechanized howitzer/artillery barrel to elevate at range.
- **Fire-on-attack — a model that animates when the unit *fires*.** Tick **Fire on attack** and the baked clip plays
  **once, on the combat action**, not on a loop: the model rests, then plays a single pass the moment the unit attacks and
  returns to rest. Proven with a **howitzer whose barrel elevates only when it bombards** — the plugin hooks Humankind's
  own combat event bus, matches the firing unit to the injected model, and triggers one playthrough.
- **First-instance rotor fix.** The engine draws the *first* borrowed-rotor pawn of a model, at the moment it's **created**,
  with its rotor ~1 unit low (a spawn race — every later instance is fine). Ticking **Re-spawn after load** makes the plugin
  watch for any such unit appearing — on a save-load, built in a city, or dev-spawned — and near-instantly re-run the game's
  own pawn rebuild (`PresentationUnit.UpdatePawns`) on it, a presentation-only refresh (no unit touched) that clears the low
  rotor. Applied to every instance as it appears (one brief flicker each) so a buggy one is never missed. Opt-in per model;
  the re-spawn delay is tunable in the plugin cfg (`Factory/RespawnDelayFrames`, default 1) for slower machines.
- **Freeze the donor's motion.** A *static* model riding an animated ground/hover donor inherits the donor's idle/move bob;
  **Freeze donor animation** pins the donor's pose so a rigid model (an airship) holds still while it still glides
  tile-to-tile — applied across *every* instance the same way animated models are (descriptor-matched + skeleton-forced).
- **Borrow the donor's animation — including *multiple* moving parts.** A model rides a donor unit's rig; injection can't
  *remove* a donor's animated sub-part (a rotor), but you can turn that into a feature: **strip your model's own rotor(s)**
  and the **donor's spinning rotor shows through**. The donor helicopter has *two* rotor bones (`Helix` main +
  `Helix_back` tail), so stripping both the Comanche's main *and* tail rotor gives it a spinning main rotor **and** a spinning
  shrouded fantail — two borrowed animations on one static model. Or give the model **its own** clip.

## Districts

- **STRATEGIC MESH FOOTPRINT + scoped-path migration (2026-08-15).** A district's zoomed-out strategic footprint is now
  its **own 3D building**, not a flat decal. The strategic fade turned out to be a **per-element GPU render-feature gate**
  (`FxEvolverMaterialLevelBuildElement.RenderFeatureSelector.SelectionFlags0`), not a camera swap — zeroing it (AlwaysEnabled)
  keeps the mesh drawing in every zoom band. On top of it: **black-and-white when zoomed out** (bind a greyscale albedo
  keyed to `RenderFeatureProvider.ComputeRenderState` of the *Topographic* band) and **flatten to a sheet** (a `size.y`
  multiplier — vertical placement is terrain-owned, so a "lift" was a proven dead end). All five settings are authored
  **per-district in the District Factory** (`footprintMesh`/BW/Flat/FlatHeight/HideDecal), falling back to the plugin
  config. Any district **migrates onto the scoped render path with one Bake** (`BakeScopedSelector` clones a
  single-building footprint template, swaps in the district's FxMesh, keeps the decals → a data-authored
  `CityMapSelector`), retiring the legacy isolate/repoint route. **Two custom districts now coexist independently**
  (breeder reactor + a Greek-temple Oracle in one game, each with its own texture + footprint) — which needed a
  per-district `ScopedState` refactor *and* moving the driving calls inside the per-district loop (they had run for the
  *last* district only). Composed **grove foliage** rendered partially (255 sub-particle cap → raise
  `DistrictMeshDensityBoost` to 32) and solid (opaque borrowed material → flip it to alpha-cutout, `_Mode=1` +
  `_ALPHATEST_ON`). Also fixed early in the session: the reactor's long-hunted "center rock" + ground twitch were
  **grafted footprint decals**, not terrain (filtered in `GraftFootprint`). Full write-up:
  [District-Dedicated-Visual.md](docs/District-Dedicated-Visual.md).

- **FOUNDATION PLINTH — planting on a cliff (2026-08-09).** A district on a coastal cliff/uneven tile
  overhung into empty air (the breeder reactor floated off the ledge). A bake-time **Foundation depth** knob
  now extrudes the building's footprint **straight down into the earth** (true world −Y, taken in drawn space
  post-rotation so it's independent of the model's import angles, then inverse-rotated back) as a solid
  concrete plinth — four walls + a floor, wound outward, cap omitted under the building. Districts render one
  atlas, so the plinth needs concrete *in* it: `AppendConcreteStrip` grows the atlas set by a fresh strip
  (noised grey albedo / neutral normal / rough concrete), slides existing content down and remaps the mesh UVs
  — **no existing texel is overwritten**. Purely bake-time: the runtime still gets one FxMesh + atlas. Two
  preview fixes rode along: re-point the preview material after the strip rewrites the atlas asset, and frame
  the camera on the **above-ground** building so plinth depth doesn't shift the view center. The health panel
  also stopped false-warning "typo" on **base-game (`Extension_*`) targets** — their definitions live in the
  game, not the project, so it stays silent (only a non-namespaced miss is a real typo). Verified in-game on
  the reactor's cliff tile. **Known limit**: a Z-fight shimmer where the plinth meets the building's own walls
  at map distance — measured to be **depth-buffer precision** (far from world origin under a huge far plane),
  not geometry; a small gap is invisible to the buffer and a large one shows a visible slot, so it's deferred
  with the shape intact (the fix path is to inset the plinth behind the building wall so a depth-beating gap
  hides).
- **HEXAGON SCULPTING — the raised platform (2026-08-09).** A district carves a raised terrain plinth
  (`UpdateHexagonSculpting` → `HexagonSculptingDefinition` → `ApplyHexagonSculptingDefinition`); a custom
  wonder's cell is empty, so the Oracle sat flat. The **fourth empty-cell fix**: a postfix forces a chosen
  index — per-entry Factory **Footprint** field + global config + a `haf_hexsculpt.txt` **live dial** (re-carve
  without relaunch, cycle ~40 shapes fast). Measured which shape to use: most districts resolve to `None`; the
  raised plinth belongs to the **emblematic quarters** (`Extension_Era1_OlmecCivilization` →
  `EmblematicAndCityCenter26`). Verified in 3D on the Oracle. Two honest limits documented: the **preview can't
  show it** (runtime terrain deformation, not baked geometry — judged in-game like PBR shading), and the raised
  platform is **not** the top-down **strategic-zoom footprint** (a separate render-mode path, still open).
- **GROUND MATERIAL — the maintained field (2026-08-08).** A district paints the terrain under it via
  `UpdateGroundMaterial` (a `(Biome × affinity)` → `GroundMaterialDefinition` resolve); a custom wonder's
  affinity has no row, so the Oracle stood on bare desert. The plugin postfixes the resolve and **forces a
  chosen ground index** — the game's own blended terrain paint, not a flat mesh. It's a **per-district** field
  in the Factory (dropdown of the game's vocabulary — grass / paved / sparse), with a global config fallback.
  Verified: `Prairie_Grassland` under the Oracle's temple and grove — the same empty-cell insight as the wonder
  visual, applied a third time, now to the terrain layer. The Factory **preview textures its tile with the real
  terrain image** — extracted from the game's shared `DefaultTextureAtlas` (resolve authoring data → atlas +
  element GUID → `GUIDToIndex` → `GetElementData` UV rect → crop the page tile → PNG per material), plus the
  material's true colour as a fallback — so the terrain-paint choice reads as real grass/pavement/sand before
  launch.
- **DE-ENC — framework filenames dropped the pack prefix (2026-08-08).** HAF is a universal framework, so its
  registry and tuning files shed the `enc_` badge of one pack: `enc_districts.json` → `haf_districts.json`,
  and likewise `haf_models` / `haf_formations` / `haf_sounds` / `haf_props` and the live-tuning `haf_*.txt`
  dials (177 references across 35 files, both repos + the git-tracked backups + the deployed data). The pack
  itself keeps its identity — `haf_packs/ENCReload/`, `pack.json`, its own skins/sounds — because that name is
  the pack, not a prefix.
- **DISTRICTS GO MULTI-INSTANCE (2026-08-08, verified with a second reactor).** A critical review of the
  district axis found its one architectural flaw: each registry entry held ONE component slot, overwritten by
  whichever district instance last refreshed — build the same district in two cities and ownership ping-ponged,
  only one tile showing the custom model. Fixed by splitting targeting from assets: each entry now tracks a
  **list of live instances** (added per `UpdateLevelBuild`, pruned via fake-null when razed) while the private
  leaf, layer clone, and texture bindings stay **one per entry, shared by every tile** — a leaf is just a
  material, and vanilla's shared selectors serve many channels the same way. The same review also flattened the
  per-frame hot path (cached reflection handles, cached texture bind slots — twenty districts now cost what two
  used to) and collapsed a drifted hand-rolled copy of the session reset into the canonical one.
- **SURFACE MAPS GO PER-ENTRY — the reactor regression (2026-08-08, same day).** The stability pass had bound
  flat neutral surface maps on *every* custom district; the temple then earned real baked maps, but the Breeder
  Reactor silently kept the neutrals — which turned its verified look (albedo over the donor silo's vanilla
  maps) into chrome domes and near-black walls, unnoticed for two days until its city was next visited. Fix,
  verified on both districts: entries with baked normal/rough atlases bind them; entries without keep the donor
  material's own maps. Lesson: a shared-code change verified on one district is not verified on the axis.
- **THE REVEAL-RAMP LEVER — wonders load complete (2026-08-08, same day).** Every session load replayed the
  bottom-to-roof level-build reveal on the custom wonder — vanilla plays the same ramp, the loading screen just
  hides it, and our swap necessarily lands after the screen lifts. Racing the loading screen was **falsified
  twice** (silent deadlocks — reaching for the render context from a plugin Update tick during the load
  sequence hangs the game with sync AND async loaders; LAW: never before `distFxManager` is tracked). The
  answer was a field dump away: `FxEvolverMaterialLevelBuildElement.fadeInOutMode {Stepped, Smooth, Instant}`,
  the appearance transition itself, encoded per element into GPU data. The wonder-path private clone sets
  **`Instant`** before its first Load — the temple stands complete the moment the tile renders. Open refinement:
  an `UpdateLevelBuild` event capture to keep the `Stepped` ceremony for wonders genuinely completed mid-game.
- **NATIVE WONDER VISUALS — the empty-cell revelation (2026-08-08).** One day after shipping, the Oracle's
  donor-district hack died of obsolescence. Three donor swaps failed in a row (Holy Site: bare tile; Natural
  Reserve: swap landed but drew nothing visible), so instead of donor roulette the visual-resolution chain got
  decompiled — and the "mod can't extend this" verdict of July collapsed: district visuals resolve through
  **criteria-matrix databases** whose rows are **plain datatable elements**, and completed wonders key their
  model **by wonder name** in a dedicated `ArtificialWonder` database. A `[RepoDump]` launch delivered the
  punchline — *our wonder's name was already indexed there, with a NULL guid*. July's `material 0,0,0,0` was
  never a dead end, just an **empty cell waiting to be filled**. Now `[WonderRow]` fills it (Temple of Artemis
  material as zero-bake proof + loaded template), the walker sources its swap template from the cell, and the
  proven isolate machinery does the rest: the Oracle renders its custom temple through the **game's own wonder
  pipeline** — native affinity, no donor anywhere, and the vanilla **bottom-to-roof level-build reveal** plays
  on the custom mesh after a reload. Donor laws measured en route (building-model + culture-agnostic families
  only; scatter families draw wrong; repository-fed families have no inline leaves) are kept in
  [docs/Wonder-Spike.md](docs/Wonder-Spike.md) as history.
- **THE ORACLE — first custom Artificial Wonder, shipped & announced (2026-08-07).** A Sketchfab Greek temple
  became a fully playable custom wonder in one arc: the district swap machinery carries `ArtificialWonderDefinition`
  unchanged (donor = a renderable district affinity; the *designed-for* native wonder affinity was measured a
  dead end — scaffolding-only material family, zero swappable leaves). Stability took a same-day triad, each
  mechanism measured: **streaming opt-out** (the private layer clone nulls its mid/hi-res material GUIDs so the
  reduction system can't stomp the injected albedo), **neutral surface maps** (the donor's bricks no longer bleed
  through), and a **session reset from the `Sandbox.Load` postfix** (save-reload had been re-pointing onto a
  corpse leaf → empty tile). Then the temple got its marble: **normal + roughness atlases baked with the albedo
  pack's exact rects** (the walls' albedo is pure white — the beauty was in the surface maps all along), area-average
  downsampling (a single bilinear tap aliases dense normals into rainbow static), relief calibrated into the data
  so preview and game agree. Card/small/tooltip portraits ride the standard UIMapper `Images` slots. Announced on
  Discord the same evening. See [docs/Wonder-Spike.md](docs/Wonder-Spike.md).
- **DISTRICT TEXTURES — the nuclear plant arc (2026-08-06).** Replacing the Breeder Reactor's model (a
  Sketchfab site-plan plant) turned one swap into three capabilities. (1) The **District Factory grew an
  embedded preview pane** — the baked mesh, textured, on a tile-sized ground square at the true in-game
  surface level, import angles live — after its first version *hid* a grounding bug by anchoring the ground
  to the model's own bottom. (2) That bug (the plant surfaced only its containment domes) exposed that the
  game plants the mesh by its origin and nothing re-grounds a rotated bake — the district bake now
  **auto-levels**: vertices shifted so the model lands lowest-point-on-the-surface *with its import angles
  applied*, any rotation combination stands level. (3) **Districts finally wear their own texture.** Three
  weeks of flat-shaded custom districts ended with two measurements: the district building layer is a
  **full-texture layer** (no atlas manager — leaves sample the layer material's bound sheet through mesh UVs,
  which is why an unbound custom mesh wore *patches of the culture's building sheet*), so texture is a
  per-layer binding — and `FxComponentRenderer.GetLayerIndexAddItIFN` **registers any output layer handed to
  it**. So the private leaf now brings a **private clone of the whole FxOutputLayer**: the game registers and
  loads it itself during the leaf's own Load, and the plugin binds the baked albedo on the clone's runtime
  materials. One tile, exact UVs, zero effect on every other building. A rect-painting design targeting the
  atlas manager was built first, falsified by the trace, and never shipped. See
  [docs/District-Visuals.md](docs/District-Visuals.md).

## Authoring tools

- **THE PIZZA BAKERY — multi-model districts (2026-08-08, verified: the Oracle's temple + a beech tree).** The
  District Factory composes MULTIPLE models onto one tile: parts bake with their own knobs, auto-ground to the
  base's floor, and merge into one mesh with super albedo/normal/rough atlases sharing one rect set — the
  runtime never learns the word "pizza" (one FxMesh + atlas trio per entry, so isolation/wonders/multi-instance
  compose for free). The dressing fought back three times, each measured: the multi-material pack
  **force-flattened alpha** (a=255) and the atlas compressed to **DXT1 (no alpha channel)** — cutout foliage
  baked as solid triangles until both were made alpha-aware; the v1 albedo-only compose dropped the temple's
  surface maps and the donor's maps **turned the marble blue** — super normal/rough maps with same-rect
  area-average blits brought it back; and the game's **shadow pass doesn't alpha-test**, so a dense leaf crown
  casts a soft solid blob (cosmetic, documented). The headline discovery: **the district shader honors
  alpha cutout** — card-foliage trees are first-class district dressing.
- **DISTRICT FACTORY HEALTH PANEL (2026-08-08, verified through its full lifecycle).** The review's last
  finding: the week's two costly failures — registry-vs-asset GUID drift and the stale mod bundle — plus
  July's data-prerequisite trap were all detectable at authoring time, and now they are. On selection, after
  every Bake, and on Re-check, the window compares every shipped GUID against the asset on disk (mismatch =
  red box instead of a silent "waiting for leaves" launch), the newest baked asset against the newest built
  Community assetbundle (bake → STALE BUNDLE warning → rebuild → clears), and the district definition's data
  (non-empty Additional Visual Levels = the guaranteed-empty-tile error; missing affinity = warning). One
  green line when everything agrees.
- **DISTRICT COMPASS ROSE + CORNER-FORWARD HEX (2026-08-08, verified in-game).** The district preview's tile
  hex was drawn edge-forward — the *unit* convention — but the in-game district cell presents a **corner**
  toward the model's forward (user-measured on the reactor). The shared hex builder gained an orientation
  parameter (units 30°, districts 0°) and the bake's hex-clip planes rotated to match the real cell walls. The
  facing arrow became a **NESW compass rose** — lines to all four cardinals, letters reading North-up — since
  a district has no facing of its own: what its author needs is map orientation, and the preview and the game
  now agree on it.
- **THE PREVIEW TRUTH ARC (2026-08-07).** Two days that turned the editor previews from bake-inspection aids
  into placement instruments a pack author can trust. The shared conventions, across the Model Factory,
  Animation Lab and District Factory panes: a **true-size tile hex** (6.93 across flats — the measured
  center-to-center tile spacing; the old ~10 square flattered every fit) pinned at the **origin plane** (never
  anchored to the model's bounds — that once hid a half-sunk district bake); **water-blue for boats** (the
  pawn's own Boat capability profile, never the name); a **forward arrow** (+Z, verified against the
  Jagdpanzer's barrel; edge-on, the six hex facings); **Center** re-frame and 2× deeper zoom. The Factory and
  the Lab now share the faithful **rest-pose FBX view** for animated entries — attempt 1 force-reimported the
  shared FBX and scrambled tiling-UV preview textures (reverted, root-caused, re-attempted read-only:
  VERIFIED). The arc's crown was the stealth helicopter's centering: the hex made a years-invisible off-center
  bake obvious, which exposed that **Position offset was silently dead on the animated path** — now applied in
  the rig conversion in true **game units** (pre-divided by the FBX import's `size/longest` factor that used
  to multiply the dial ~3×) — and that **donor-clip models are re-anchored by the donor rebase** (in-game
  position ≠ FBX position; three launches burned misreading placement over a district tile). Previews now show
  donor-clip entries **footprint-centered** — the measured approximation, ±0.5 units on the helicopter, honest
  caption included; exact rebase-in-editor prediction is the documented open end. See
  [docs/Donor-Clip-Flight.md](docs/Donor-Clip-Flight.md) and [docs/Editor-Tools.md](docs/Editor-Tools.md).
  **CORRECTION (same day, user-caught):** the animated-path offset bake and the donor-clip approximations were
  built on a **doubled signal** — the plugin had applied the registry `position` at RUNTIME all along
  (`ApplyPositionOffset`, per frame, pawn frame, game units), so baking it too made every animated model carry
  the offset twice: the helicopter flew at *exactly 2×* its dialed height, and the "calibration" launches were
  fitting multipliers to runtime + bake in two different frames. The user's arithmetic (halving the dial restored
  the exact old height) exposed it. Unwound: bake-side application removed, footprint-centering removed, previews
  draw the **runtime offset live** — one dial, one application, no re-bake to nudge a model. The lasting morals:
  *grep for a runtime consumer before resurrecting a "dead" knob*, and *a knob that seems to need calibration
  usually has two writers*.
- **Vehicle Lab: helicopters + interior-part detection (2026-08-03).** Two new part roles rig a **rotorcraft** the
  same no-Blender way as a wheeled vehicle: **Rotor** (`R`) and **Tail rotor** (`L`). Each rotor fuses into **one
  hub bone** (proximity clustering would shred a wide blade disc into pinwheeling halves — the RAH-66's 18-unit
  disc proved it): the main rotor pivots on its central hub part and spins about that hub's own *pole-to-pole*
  axis, the tail fan pivots on its blades' centroid and spins about the axis *perpendicular to the duct ring*,
  with an own Auto/X/Y/Z override + **yaw/pitch trim sliders** for the last degrees by eye. Rotors are exempt from
  the wheels' rolling-contact speed scaling (it span the small tail fan ~3.6× too fast), Verify understands them
  (1 hub per group; car-only symmetry checks skipped), and new preview aids — **Pause**, **one-frame step ◀/▶**,
  a **Level line** at rotor height — make the axle judgeable. Preview-verified on the RAH-66; in-game bake pending.
  Same day, the probe gained **escape-ray visibility classification**: every part is tested for a straight
  line-of-sight to infinity, and a **Visibility switch** (All / External / **Interior only**) surfaces the parts
  that are *provably never visible* — cockpit gear, engine guts — for a one-key **Ignore** sweep. On the RAH-66 it
  found 47 interior parts worth **28% of the model's vertices** (11,042 → 8,651 in the generated rig), budget
  returned to the shared GPU vertex pool. Deliberately conservative: a part that peeks through any opening counts
  as external.
- **The Vehicle Lab — any static vehicle model becomes that unit, no Blender knowledge (2026-07-25).** A dedicated
  window "vehicleizes" a raw model: headless-probe its parts (a 3,350-shard game rip included), mark wheels &
  turret with a keyboard-driven review UI (zoom-highlight preview, classification filters, height-slab sliders,
  save/load **recipes**, a clustering-accurate **Verify** report), and it builds the rigged, LINEAR-`Spin` GLB the
  animated path consumes — wheel shards **clustered per hub** so spokes revolve around the axle, one mesh per bone,
  the rip's stowaway skeleton stripped. **Verified in-game the same day: the shipped ArmouredCar now runs a
  Lab-generated rig** — grounded, turret aiming (axis Y on generated rigs), muzzle flash re-anchored on the
  `Turret` bone. Rips that ship **already rigged** (`SKM_`) get a **fast path**: the probe detects the skinned
  artist skeleton and the Lab marks *bones* instead of shards — Spin authored straight onto the source rig,
  weapon/socket bones preserved (it inherits the artist's weighting; the shard flow stays the quality reference).
- **The Animation Lab — animation authoring in its own dialog (2026-07-18).** `Tools ▸ HAF ▸ Animation Lab` docks as
  a tab beside the Factory: the Factory owns the *model* (file, transform, size, shading), the Lab owns the
  *animation* (clip + bone-filter pickers, fire-on-attack, deploy-on-stop + recoil, and **Save (no bake)** for
  runtime flags). Settings are mutually exclusive between the windows and **enforced at bake time** — each window
  rebases on the freshest registry entry and writes only its own fields, so stale copies can't clobber each other.
  Geometry re-processing is **automatic** (the Blender step re-runs exactly when one of its inputs changed); the old
  "Reuse extracted" checkbox is now purely **"Keep extracted texture (hand-edits)"**.

## Textures & meshes

- **Multiple static models live**, no new code each: a **Zeppelin**, an **LCAC Hovercraft**, a fully-textured **USS
  Zumwalt stealth cruiser**, and a **RAH-66 Comanche** helicopter — correct orientation, correct skin, at the waterline.
- **Heavy / single-sided / multi-material meshes, handled** — a built-in vertex reducer, a winding fix + double-sided
  fallback for CAD "sketch" meshes, height-based UVs, and an N-material atlas packer. Formats: GLB / glTF / OBJ / FBX /
  `.blend`.
- **Correct, isolated textures.** Custom skins map right-side-up out of the box (the glTF-V-top vs OBJ/Unity-V-bottom
  convention is reconciled during OBJ import, and off-tile UVs — a skin mapped into the V 1→2 tile relying on wrap — are
  shifted back into range so they don't collapse to a flat smear), and each model gets a private `FxOutputLayer` so its
  skin never bleeds onto the donor.
- **Tune the skin, shrink the bundle.** Bake-time **Albedo brightness / saturation** lift a dark or washed-out skin (the
  injection ships *flat* albedo — donor PBR neutralized — so a shiny/dark source reads muddy without this); a **Keep black**
  toggle preserves an intentionally black material (a glass canopy); and **Atlas size** (256–2048, default 512) + DXT1
  compression keep each shipped skin ~0.1–2 MB. Bake *inputs* live in `Assets/FactorySource/` — out of the shipped mod, so
  the licensed source models are never redistributed.
- **Strip parts of your model at bake time.** A "Strip parts" field deletes named objects (+ children) from the source
  mesh before baking — the mirror of Hide-donor, on *your* model. Drop a helicopter's own rotor, a crew figure, a weapon
  pod… Name-Pick reads objects straight from the GLB/glTF. Proven removing the Comanche's rotor blades.
- **Retexture / recolour without a bake.** A separate **Unit Retexture** window reskins an existing unit at runtime —
  a hot-loaded PNG or a live Desaturate + RGB adjust on its own atlas — isolated per unit, free on the vertex budget.
  Works on **baked custom models** too (the PNG replaces the baked atlas — recolour without a re-bake), with a live
  in-editor preview of the exact skin that will be injected.

## Audio

- **Unit movement audio — engine sounds & custom WAVs.** Injected/retextured units are silent on move (the game's per-ship
  engine sound rides an audio-service path our re-loaded units never fire). The plugin restores it — playing the game's own
  sound **by name** (works from the *first* unit, no capture; F8 **Dump Sound Catalog** lists all ~845 event names) — or
  **any custom WAV you drop in**, as a **Start (spool-up) → Travel (loop) → Stop (spool-down)** sequence with per-clip
  volume, driven by the dedicated **Sound Studio** editor window (with in-editor ▶ preview). Runtime-only, no bake.
- **Creature voices — silence the donor, add your own growl and attack roar.** A borrowed animal donor drags its Wwise
  voice along (the Abomination's bear donor growled and mauled through every re-skin); `silenceDonorAudio` drops it at
  runtime. In its place: an **Idle growl** WAV on a jittered interval with a **one-voice radius** (a 5-stack snarls one
  pawn at a time, not in unison), and an **Attack sound** fired at attack *commit* — camera-anchored so it stays audible
  at battle zoom, with a **start offset** that skips a WAV's silent windup so the impact lands on the swing. A **Death
  sound** (rattle/scream as a pawn falls) and a **Battle-start war cry** (once, the moment a battle begins with the unit
  in it) complete the arc: alive → to arms → fighting → gone. (Growl + attack verified in-game 2026-07-23; death + war
  cry built, in-game verification pending.)

## Multi-mod & safety

- **Multi-mod — merge packs from many authors (2026-07-19).** The runtime is a **Humankind Asset Framework** host, not just
  ENC's loader: it merges ENC's base registry with any number of third-party **packs** dropped in `BepInEx/config/haf_packs/`,
  so a modder augments their own units with a custom model / texture / sound by shipping just a config file + assets — **no
  ENC edits, no code**. Pack resolution is **enforced**: duplicate `modId`s rejected, `dependsOn` validated, load order
  topologically sorted over `dependsOn`/`loadAfter` (cycles broken loudly), **declared `overrides` replace** the targeted
  entry, and an undeclared same-pawn clash stays first-loaded-wins, logged loud — no silent overrides. Every load writes a
  `haf_load_report.txt` with the resolution decisions.
- **Backup & Restore — a safety net for the un-versioned assets.** ENCReload's git tracks only `Assets/Databases`;
  a **Backup and Restore** editor window snapshots everything else (editor tooling, source & baked models, databases,
  `Tools/`, and the live BepInEx runtime config) to a timestamped, manifest-backed folder on `D:`. Restore is guarded —
  it auto-snapshots the current state first, copies back **additively** (never deletes work you've added since), and
  verifies file counts.
