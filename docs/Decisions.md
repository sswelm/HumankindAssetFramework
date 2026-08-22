# Decisions

Short records of **settled decisions** — the *why* behind choices that would otherwise be re-litigated, or
reverse-engineered from the code months later. Each entry is the decision, the reasoning, and (where useful) what was
rejected. Newest first. This is the fast answer to "why is it like this?" without archaeology — and the first place to
check before proposing a change to any of them.

---

## Per-frame cost is a number in the F8 panel, never an estimate (2026-08-21)
Asked what a day of changes cost per frame, the answer given was a reasoned estimate: "under 1%". The first
measurement read **5.7 ms/frame — 16.7% of a 30 fps frame, 15× the estimate** — and not one of the causes was the
day's changes; all were long-standing per-frame scene scans, unthrottled retries and reflection density that nobody
had ever timed. Six measured rounds later it is ~0.9 ms.
**Rule:** `FrameCost` stays on. Every per-frame entry point runs inside a bucket; a new poll, hook or per-pawn path
gets a bucket when it is written. Performance claims about HAF are read off the `[FrameCost]` line (µs/frame per
bucket, ns per pawn-add), never reasoned from the code. When a bucket surprises, SPLIT it before touching anything
— twice today a fix aimed at the wrong cause (reflection on the pose entry; the raycasts) until a sub-bucket showed
where the time actually was. And when a target can't be found by reading, ask the DLLs headlessly (`tools/typeprobe`)
before spending a game launch.
**Why it is written down:** the estimate was confident, specific, wrong by an order of magnitude, and would have been
accepted. The meter costs two `Stopwatch` reads per bucket per frame.

## Thread safety is about shared HEAP, not the Unity API (2026-08-21)
Every off-main-thread path in HAF (the sim-thread combat/battle hooks, `Sandbox.Load`, `FacingPersist` save/load)
was correctly guarded against touching Unity objects — queues, volatile flags, main-thread drains. Three separate
comments then declared paths "thread-safe" because they were *"pure managed reads"* or *"pure reference-nulling"*.
Both were true about Unity and false about the heap: `GetMember` hides a dictionary **insert** behind a read-shaped
call, and `ResetDistrictSessionState` hides ~13 `Clear()`s behind "nulling". Either, racing the per-frame main-thread
readers, is a bucket-chain corruption — a freeze with no exception and no log line, the one failure class none of
HAF's loud-failure machinery can see.
**Rule:** for any code reachable off the main thread, the question is *"what shared mutable state does this touch —
including inside the helpers it calls?"*, not *"does it call into Unity?"*. A static collection touched from two
threads is either a concurrent type, locked on every access, or published-once-and-snapshotted (`entries`). "Main
thread only" in a comment is a claim to verify by grepping the sim-thread hooks, not a guard.
**Why it is written down:** the 07-19 adversarial review signed off "cross-thread sample locking" — and it *was*
clean; the sample lists were locked. The caches and the reset simply weren't on anyone's list, because their call
sites look like pure functions. The fix was ~20 lines; finding it took a review that asked the other question.

## Every RenderTexture readback restores `active` in a `finally` — no exceptions (2026-08-21)
HAF reads pixels back off the GPU in three places (`BuildAdjustedAtlas`, `MakeGrayCopy`, `ToReadablePng`): take a
temporary RT, `Blit` into it, set `RenderTexture.active`, `ReadPixels`, restore. If a throw escapes between setting
`active` and restoring it, the active render target is left pointing at our temporary — which **corrupts the next
draw**, i.e. a whole-screen artifact produced by an off-screen texture utility. The RT and the half-built `Texture2D`
leak with it.
**Rule:** capture `prevActive` *before* the try, null-init the RT and the texture, and restore/release/free in a
`finally` — restoring `active` FIRST. Never on the success path only.
**Why it is written down:** this has now happened three times and been noticed twice. Two sites were hardened when
the bug was first understood; `ToReadablePng` was missed and sat unfixed until 2026-08-21, found while chasing an
unrelated visual artifact that turned out not to be HAF at all. It never fired in practice (its only caller is the
operator-triggered atlas dump) — which is exactly why nobody looked.
**Not testable:** `RenderTexture`/`Blit` need a live Unity render loop, so there is no unit test and no gate check
for this (a regex guard over method bodies was considered and rejected as too fragile to be worth the false
positives). The guard is that all three sites now have one shape — **if you add a fourth, copy an existing one.**

## To test the untestable, move the DECISION out of the method that does the I/O (2026-08-20)
Most of the plugin is reflection against a live game inside Unity and cannot be unit-tested. The decisions buried
in it usually can be. **Rule:** when a method mixes I/O, engine access and a *decision*, move the decision to a pure
static that takes plain data and returns plain data; leave the I/O where it is; test the pure half. `SmokeVerdict`
was the first of these; `DialConfig` (the four `haf_*.txt` dials) the second, which deleted 85 lines of duplicated
hand-rolled parsing from four `Poll*` methods and took the suite 120 → 329.
**What the extraction must carry with it:** a pure function that *reports* what it could not understand. All four
dials silently `continue`d past any line they did not recognise, so a typo produced a working plugin that ignored
the setting — the "silently disarmed" class, in the one part of HAF a user hand-edits mid-session.
**Rejected:** testing through the `Poll*` methods with a fake filesystem. It would have pinned the I/O, not the
decision, and left the parser just as tangled.
**The guard rails, because extracting SHIPPED code can change it:** keep the original inline loop as a *legacy
parity oracle* in the tests (values compared over a corpus; diagnostics excluded, since adding them is the point),
and *mutate* the new code to prove the suite bites. Both earned their keep immediately — the oracle found a latent
bug (an empty bone name matched bone 0, because `IndexOf("") == 0`), and one of six mutations correctly *passed*,
being a genuinely equivalent implementation rather than a gap. See [Testing.md](Testing.md).

## A tool is not trusted until it is DRILLED — review alone earns nothing (2026-08-17)
The backup system passed a critical review (4 defects found and fixed) and was declared trustworthy — then its
FIRST live recovery drill surfaced a fatal gap (the backup never contained the model registry) plus eight more
product defects the review could not see (all-or-nothing restore, stale windows, confusing list, churn noise, …).
**Why the review missed them:** it reviewed the day's diff, not the system it stood on; and it never EXECUTED the
feature once — static reading finds code defects, only use finds product defects. **Rule:** for any user-facing
tool, the ladder is written → reviewed → **drilled** (at least one real execution of the primary scenario, e.g.
remove-and-recover on live data) → trusted. A review may claim "reviewed", never "trustworthy". Same lesson at
framework scale: the 08-17 verified review named "zero external validation" as HAF's blind spot — the drill is
the internal substitute until real adopters exist.
The converter's source, csproj, and pinned `SharpGLTF.Core.dll` live ONLY in this repo; ENCReload holds just the
deployed `glbconv.exe` + BUILD.md. **Why:** the previous two-copies arrangement split-brained — each copy accumulated
a verified fix the other lacked, and the 2026-08-16 rebuild shipped the deployed exe with the T5 mirrored-winding fix
silently regressed. A cross-repo file copy without a sync guard eventually ships a regression. **Rule:** never
reintroduce a second source copy; after any rebuild, A/B-diff old-vs-new OBJ output before deploying (procedure in
ENCReload `Tools/glbconv/BUILD.md`).

## A cross-repo copy is either authoritative or it doesn't exist (2026-08-01 / 2026-08-17 / **2026-08-21: no exceptions**)
Every file has exactly one home. The authoritative editor scripts live in ENCReload (`Assets/Scripts/Editor/`); the
converter source lives only in `baker/glbconv/`.

**2026-08-21 — the last exception was removed.** The editor `.cs` files in `baker/` had been carved out as a
"deliberately stale reference snapshot": documented loudly in `baker/README.md` rather than deleted, because in
2026-08-01 a *blanket* delete of `baker/` was genuinely unsafe (the folder also held the live `glbconv/` and a
`Tools/` Blender-script copy). All 13 files, ~7,000 lines, are now **deleted**. What changed:
- the entanglement is gone — the Blender copies went on 2026-08-17, so a **targeted** delete of just the editor
  `.cs`, leaving `glbconv/` and `reactor_silhouette.py`, carries none of the 08-01 risk;
- the carve-out was documenting a hazard instead of removing it. The snapshot's `ModelDef` was missing fields the
  plugin reads, so baking from it **silently omits** them — default scale, default phase spread, no error;
- the same disease had already shipped a regression once (the glbconv split-brain, CHANGELOG 08-17). "Documented"
  is not a sync guard;
- and it gets worse on release day. A maintainer knows the snapshot is a trap; an adopter finds plausible-looking
  editor source in the repo, bakes from it, and ships a quietly broken pack.

**Rule (now unqualified):** a cross-repo copy is either authoritative or it does not exist. If keeping one seems
necessary, the honest options are a submodule or a link — never a snapshot with a warning on it. Nothing is lost to
a delete: git history keeps the bytes, and the authoritative copy is one repo away.

## Pack load order follows Humankind's mod order (2026-08-16)
HAF packs load in the same order Humankind loaded their runtime **modules** — not alphabetical, and not a base-priority
flag. **Why:** a HAF pack is the content-extension of a HK mod, so borrowing the platform's own order makes conflicts
resolve exactly as the player's mod manager dictates; the framework invents no competing rule. **Rejected:** a
`"base": true` flag (privileges one pack in a supposedly neutral framework) and plain alphabetical order (arbitrary).
**How:** match each pack to its module by folder/file name (== module Name, auto) or an explicit `module`/`moduleGuid`;
order by the module's load index via `Amplitude.Mercury.Runtime.IRuntimeService.GetRuntimeModules()`. See
[Multi-Mod.md](Multi-Mod.md).

## Make reflection drift loud — don't try to remove reflection (ongoing)
HAF binds to ~1,475 game internals by reflection; a closed engine leaves no alternative. The strategy is **not** to
eliminate reflection but to make its *drift visible*: every game-type name lives in one `GameBinding.<Type>` accessor, a
startup catalog validates types + members, and a machine-readable `haf_bindings_report.txt` names anything a game update
breaks (headless-checkable). **Why:** removing reflection isn't possible; turning a game patch into one diffable report
line is. See [Framework-Review.md](Framework-Review.md) (reflection-fragility A1–A5).

## Fail loud, never silently mis-produce (ongoing)
Wherever HAF could ship a *broken* result silently — a mis-bake, a stale cache reuse, an empty district, a zero-GUID
munition, a vanished UV — it aborts or warns loudly instead of reporting success. **Why:** a silent wrong result costs
hours to diagnose; a loud one costs seconds. Pervasive: the bake hard-fails, the district `selectorGuid` clear, the
glbconv multi-tile warning, the honest "Baked, but REGISTRY SAVE FAILED" status, the pre-push gate.

## The pre-push gate over ad-hoc manual checks (2026-08-16)
The fast guards (build, tests, editor compile-check, schema parity) run as one `Tools/check.sh` per repo, wired as a
pre-push hook. **Why:** "discipline you must remember" isn't a net; standing the gate up immediately caught three latent
schema drifts. Heavy guards (Blender golden-master, in-editor Feature Test, in-game binding report) stay **out** of the
sub-minute gate on purpose. See [Testing.md](Testing.md).

## The Factory owns the model; each Lab owns its axis (settled)
The Model Factory lists ALL entries and owns model geometry / transform / shading + its bake GUIDs; the Animation Lab
owns animation config, the Sound Studio audio, and so on. Cross-window writes go through a **fail-safe ownership rebase**
(start from the saved entry, overlay only the owned fields) so a newly-added field is preserved by default. **Why:** the
old denylist rebase was forgotten four times (silent field reverts); the fail-safe inversion killed the drift class.
Don't re-litigate the split. See [Framework-Review.md](Framework-Review.md).

## No `ModelEntry` split / POCO refactor (declined) — **scope narrowed 2026-08-21: the clip roles became a table**
The plugin's `ModelEntry` is a large class; a proposed split into smaller POCOs was **declined**. **Why:** the churn
touches the settled ownership model and the reflection-cached per-frame hot path for no proven bug; the drift it was
meant to prevent is already handled by the fail-safe rebase + the schema-parity guard. The big-bang split stays declined.
**What changed (2026-08-21), and the evidence that justified reopening one slice:** the premise *"no proven bug"* had
aged. The nine clip roles were nine hand-expanded field families (4 guid ints + collection + animId + duration, ×9 ≈ 63
fields), and every "all roles" site was a hand-written list of nine edited in lockstep. That shape produced **two
shipped bugs**: `AnyStateRole` gated on `moveAnimId` alone, so a move-less state-driven model armed fires that never
animated (critical-review #8); and the smoke test's wiring dropped the `alc` component (the reason a 36-int reflection
test had to exist). So **that slice** is now one table — `ClipRoles.cs`: a `ClipRole` enum, a `ClipBinding` per role,
`ModelEntry.Roles[9]` — and every "all roles" site (load, resolve, re-arm, preflight, smoke, `AnyStateRole`) is a loop.
The named accessors (`e.attackAnimId` …) remain as sugar *into* the table so the per-role call sites and the hot path
are untouched; the pack contract (the `clip*` JSON arrays) is unchanged on both parse paths. **Rule going forward:** a
restructuring of `ModelEntry` is on the table only with a *proven* bug from the shape it removes, and only as the
bounded slice that removes it. The per-unit runtime dictionaries stay as they are — no bug, hot path, threaded.

**Re-examined 2026-08-22 — split still declined; the real complaint fixed another way.** A review asked to reopen the
split, arguing that Architecture.md §2's four-locked-field table is the shape leaking into the docs, and offering **the
two 08-21 data races as the "proven bug"**. Checked: *neither race was in `ModelEntry`* — one was the `memberCache` /
`fieldCache` **statics** in `UniversalInject.Reflection.cs`, the other `ResetDistrictSessionState` clearing
**DistrictInject's** collections off-thread. A split would have prevented neither, so the trigger above is not met.
Two further facts narrowed it: the split's cost was quoted as ~200 call sites, but the four locked fields have **53**
between them; and config/state separation already *partly* exists by construction — the 68 inherited members are the
shared `Haf.Schema` config, so the mixing is only within the 92 own members. What the review was right about is
**memorisation**, and looking for its evidence found worse than it claimed: of `ModelEntry`'s 23 mutable collections,
only **6** stated any thread discipline. So the fix is the pattern this codebase now uses everywhere — declare and
enforce (`[MainThread]` / `[Locked]` / `[Concurrent]` + `ModelEntryThreadTests`, Architecture.md §2) — which covers all
24 mutable fields instead of 4, touches no call site and no hot path, and leaves the split available if a bug ever does
trace to the shape.

## >127-bone rigs: pair-merge on the deploy path, not zero-weight slimming (2026-08)
The GPU skinning wall is a per-vertex **bone-index** limit of **128** (indices break past 127) — *not* 256. A rig with
verts weighted to bones past index 127 breaks. `deploy_convert` pair-merges instanced link chains to ≤126; `rig_anim`'s
zero-weight-leaf slim does **not** rescue a rig with >127 *weighted* bones (the mech's collapsed "wings"). **Why:**
slimming to a total count (e.g. 240) never moves a vertex off a >127 index — only merging does. See
[Animation-Pitfalls.md](Animation-Pitfalls.md).

## Rotation-only custom animation (engine contract)
Custom clips animate by **rotation** (with constrained translation), bind == frame 0, on a scale-1, name-ordered
skeleton. **Why:** the measured Amplitude GPU pose path consumes rotation reliably; free translation/scale channels
scramble or are ignored. This is an engine contract, not a style preference. See
[Animated-Runtime.md](Animated-Runtime.md) / [Animation-Pitfalls.md](Animation-Pitfalls.md).

## Framework is neutral (`haf_*`); ENC is a branded pack (2026-07-19)
Everything framework-level is `haf_*` and name-neutral (`HumankindAssetFramework.dll`, GUID `community.humankind.haf`,
menu root `Tools ▸ HAF`); ENC is the reference **pack**, branded like any pack, and a third-party pack never touches an
`haf_*` path. **Why:** the framework is meant to be adopted by other modders; the flagship content is one pack among
equals. See [Multi-Mod.md](Multi-Mod.md).

## Conflicts: first-loaded-wins + declared overrides, never silent (2026-07-19)
When two packs target the same pawn, the first-loaded one (now = earlier in Humankind's mod order) keeps it, logged
loud; a replacement must be **declared** in the pack's `overrides`. No implicit or silent overrides. **Why:** silent
overrides make a multi-pack setup undebuggable.

## Session-scoped state is declared, not remembered (2026-08-21)

**Decision:** every static collection in the plugin carries a lifetime attribute — `[SessionScoped]` (the
`SessionState` registry clears it on the scope's reset), `[SessionScoped(Manual = "site")]` (hand-reset at a named
seam) or `[ProcessLived("why")]` — and a reflection test fails the build on a bare one.

**Why:** the project's most-repeated bug class was "a static survived re-arm" (the Oracle incident, the `_DRILL`
pack-data bug, the tank-destroyer donor skin). The rule against it lived in a comment and a hand-list of ~60
`.Clear()` calls across sixteen partial files, so it held only as long as every author remembered it. The first run of
the test found two descId-keyed maps that had never been cleared. Enforcement costs one attribute per field and
runs in CI with no game.

**What it does not do:** prove order. The hand-written lines around the bulk clear still own the sequence, and
non-collection statics (`bool`/`int` latches) are outside the rule. A triage reason on `[ProcessLived]` is
mandatory — "constant" and "type cache" are fine; an empty one fails.

## The injecting 18k lines are verified in the live game, on demand — not through a fake object model (2026-08-21)

**Decision:** the reflection half is verified by the in-game smoke test reading the *engine's* state on demand
(live pawn slots, the pose hook's own timestamps, a fresh sub-pawn scene audit), plus the pure-extraction pattern
for decision logic. No fake-object seam under the reflection accessors; no always-on verification.

**Why:** a reviewer proposed a thin seam over `GetMember`/`SetMember`/`Invoke` with a recorded type graph so the
decision logic could run in xUnit. Measured: reflection is *not* funnelled — ~660 accessor calls but also ~350
direct `AccessTools` and ~400 `.GetType()` sites, ~1,450 in all — so the seam would be a rewrite before the first
test. And a type graph gives types, not values; the decisions depend on runtime values the game assigns (ids per
session, which collection holds a squadron, a packed property). Every drill-caught bug this week was one of those
assumptions being wrong — a fake built on the same assumptions would have passed them all. The user also ruled out
always-on automation on performance grounds: checks run when the button is pressed, never per frame.

**What it means in practice:** a drill-caught bug earns a smoke check that reads the engine at the level the bug
lived (see Editor-Tools.md, Smoke Test), and decision logic that *can* be pure is extracted and tested (`PoseMath`,
`DialConfig`, `PackTuning`, `MergeModels`, `GatherLivePawnFacts`, …). "Coverage target = theatre" still holds;
"the seams are untestable" does not — they are testable in the one place they are real.

## A focused unit suite, not broad coverage or an in-game test framework (settled)
The plugin has a bounded suite (**111 tests**) over the **pure logic that runs outside the game** (parse / schema /
reflection-resolution / the smoke-verdict rule); everything engine-coupled is verified by in-editor instruments (Feature
Test, smoke test) plus in-game. **Why:** the game can't run in a test host, so a coverage *target* over engine-coupled
code would be theatre — the suite guards where bugs have actually hidden and stops there. Broad automated testing is
revisited at the public-package push. See [Testing.md](Testing.md).
