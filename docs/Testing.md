# Testing

HAF is verified in **three tiers**, each a machine, each at the level where its bug class actually lives:

| Tier | Runs | Guards |
|---|---|---|
| **Unit tests** — **713 as of 2026-08-29** | `dotnet test`, the pre-push gate, CI | the pure logic: registry/parse/era, pack resolution + merge + tuning tables, pose math, dial config, the session-state rule, the smoke **verdict and classifiers** |
| **Headless game checks** | `tools/check-catalog.sh` in the push gate; `tools/check-bindings.sh` on demand / after a game update | **two halves of one claim**: `check-catalog.sh` proves the catalog **covers the code** (every by-name literal at a reflection site is catalogued or allowlisted with a reason), `bindcheck` proves it **resolves** against the real DLLs; `typeprobe --find` / `--exact` locate a seam or a member's owner before a binding is written |
| **In-game smoke test** — `[load]` automatic, `[full]` on the F8 button | every load (a few ms, once), and on request | the injecting half, read from the **engine**: bindings, registry, roles, assets, sounds, files, GPU budget, district tiles and textures, patched seams — and, on the button, every live pawn on *our* skeleton, pose-hook liveness, the sub-pawn walk vs a scene scan, the write-back self-test |

> **This page owns the test count.** Nowhere else states it. On 2026-08-23 this file said **589** in the table above
> and **681** twelve lines further down, while `dotnet test` reported **705** — the same drift
> [Shared-Schema.md](Shared-Schema.md) already fences off for the field count, caught here by a review rather than by a
> guard. Re-check with `dotnet test Tests/HumankindAssetFramework.Tests.csproj -c Release` and read the `Total:`.
> The number counts expanded `[Theory]` cases, so it moves faster than the count of test *methods*.

The unit suite is a deliberate, bounded suite, not a coverage target: it guards the functions where bugs have actually
hidden, and stops there on purpose. What it cannot reach — the ~18k lines that reflect into a running game — is not
left to eyes: the smoke test reads the engine's own state, and its load tier needs no one to press anything.

```
dotnet test Tests/HumankindAssetFramework.Tests.csproj -c Release
```

## The fast gates — two lanes, `check.sh` and CI

The fast guards used to be separate scripts you had to remember to run. They're now one in-repo command
(`tools/check.sh`), wired as a **pre-push hook** — and, since 2026-08-23, the guards that need nothing but source also run
in **GitHub Actions**. Both lanes matter, and for different reasons:

- **The hook** runs *everything*, including the guards that need a licensed Unity install or the game. It is the
  complete gate, and it is the only place some checks can run at all.
- **CI** runs the source-only subset. A hook is **per-clone config** (`git config core.hooksPath …`) that a
  contributor may never have set, and `git push --no-verify` — or a GitHub web edit, which runs no hook whatsoever —
  walks straight past it. CI is the lane that survives all three.

| Surface | `tools/check.sh` (pre-push hook) | also in CI | ~time |
|---|---|---|---|
| **Runtime + shared contract** | `dotnet build` · `dotnet test` · docs guard · binding-catalog surface · hot path · parse shape · member shape · schema parity | all source-only checks | seconds |
| **Editor package** | Roslyn editor compile-check · schema parity · hand-list gate | parity + hand-list; compile check stays local | ~30 s |

The one guard CI cannot run is **`tools/editor_compile_check.sh`**: it needs a licensed Unity 2021.3.1f1 install
(`UnityEditor.dll`, the MonoBleedingEdge 4.7.1 profile, every `UnityEngine` module), none of which is
redistributable or present on a hosted runner. It stays in the hook, where the Unity install already is. So the
editor's compile check is the one check a `--no-verify` still gets past — worth knowing before you use one.

### Schema parity is in-repo and mandatory

The editor package, shared `Haf.Schema`, runtime `ModelEntry`, and regex fallback now live together. Therefore
`tools/check_schema_parity.sh` has no sibling checkout and no best-effort `[SKIP]` path: every push compares the
writer, generic reader, regex fallback, GUID hand-lists, and shared types from the same revision. A missing fallback
key is still dangerous because malformed JSON can silently lose it, but the guard is now symmetric by construction.

The parity and hand-list guards were **fault-injected before being trusted**: a UI-edited field with no ownership-list
entry and a deleted `Regex.Matches` line each turned the matching step red and named the offending field.

### The docs guard (`tools/check-docs.sh`)

The docs publish **three** ways — the repo, the [Pages site](https://sswelm.github.io/HumankindAssetFramework/)
(which rewrites relative `.md` links via `jekyll-relative-links`), and the wiki (`tools/sync_wiki.sh`) — and all
three resolve *relative* links. So one moved page breaks three surfaces at once, silently. The guard checks:

1. **every relative Markdown link and `#anchor` resolves** — anchors are derived from the target's headings using
   GitHub's lowercase/punctuation/whitespace and duplicate-suffix rules;
2. **every page in `docs/notes/` opens with the `ARCHIVED NOTE` banner** — the convention that makes the
   maintained-vs-archived split mean something rather than being a folder name;
3. **no basename collides across `docs/` and `docs/notes/`** — the wiki page namespace is flat, so a collision
   would have one page silently overwrite the other;
4. **schema version and the shared-field count agree with code**, rather than with another prose copy;
5. **maintained Pages docs do not use `../` links** that Jekyll would publish outside the project site;
6. **a fresh wiki generation succeeds**, contains no empty/missing internal targets, and its tracked sidebar includes
   every maintained page;
7. **retired pre-package claims stay out of current guides** (cross-repo editor paths, missing helpers, hardcoded
   guest identity). Historical review pages remain intentionally untouched.

Fault-injected: a dead file link, a valid file with a nonexistent heading anchor, a banner-less note, and a planted
`docs/notes/Textures.md` collision were each caught with a named failure, and the baseline returned to green.

Run it any time by hand: `bash tools/check.sh`. **Enable the hook once per clone:**

```
git config core.hooksPath tools/git-hooks
```

(Casing matters on case-sensitive filesystems: this repo's folder is lowercase `tools/`; ENCReload's is `Tools/` —
the mismatch has already eaten files once, commit `db40e73`.) The hook (`tools/git-hooks/pre-push`,
version-controlled) then blocks a failing push; bypass only in a real emergency
with `git push --no-verify`. Deliberately **not** in the gate (too slow / need Unity, Blender, or the game): the Blender
golden-master `deploy_regression.sh`, the in-editor bake tests, and the in-game binding report — those stay manual.
The in-editor tests all run from **one window** — `Tools ▸ HAF ▸ Bake Tests…` (Smoke / Features / Conversion rows,
each with a plain-language explanation, live per-row PASS/FAIL, and a durable `Logs/haf_bake_tests_report.txt` per
run; see [Factory-Manual.md](Factory-Manual.md) §11 — including what SKIPPED means, what a fresh
package install reports, and which rows need Blender). The
gate earned its keep on day one: standing it up surfaced three latent schema drifts (a wrapper field the plugin read but
the baker never wrote, two runtime-only keys, and a `float?`-cast the parity script mis-classified), all fixed to green.

### The hand-list gate (`tools/check_handlists.sh`) — four blocks

This project's signature bug class is a **hand-maintained list of fields that must stay in step with a type**. Each
instance shipped a real bug before it was gated, and each gate compares the list against the type mechanically, so
the next added field fails the push instead of being silently dropped:

| Block | The list | The bug it shipped |
|---|---|---|
| Factory ownership rebase | every UI-edited field re-applied on Save | `combatZ` silently reset to 0 the day it landed |
| Animation Lab ownership rebase | same shape, the Lab's 63 fields | same class |
| Vehicle Lab recipe round-trip | every `Recipe` DTO field written **and** restored | the canoe's wave config vanished; took GLB forensics to recover |
| Clone GUID reset (2026-08-22) | every `int[4]` GUID cleared on a copy | `clipIdleAlt2` inherited, pointing the clone at the **source's** ClipCollection |

Each block is drilled the same way: plant the omission, watch the gate name it, restore. A gate nobody has seen
fail is not yet a gate.

### The dead-sentinel gate (`tools/check-member-shape.sh`)

Sibling of the dead-default `TryParse` gate, one layer down. The banned shape:

```csharp
bool loaded = true; try { loaded = Convert.ToBoolean(GetMember(unit, "IsLoaded")); } catch { }
if (!loaded) continue;
```

`GetMember` swallows its own exception and returns **null** for a missing or renamed member — and
`Convert.ToBoolean(null)` is `false`, `Convert.ToInt32(null)` is `0`. **They do not throw.** So the `catch` never
runs, the initializer is dead, and the variable takes the converted-null value instead of the default written
beside it. Two live sites had exactly this and then skipped their work on it: on a game rename the respawn pass
and the vanilla re-scale would each have stopped running, silently and permanently.

The fix is the one the `ParseFloat` policy already states — **the fallback is a return value, never a variable the
call can overwrite** — plus a `Try*` pair for the sites whose intent is *"if I cannot read this, leave the thing
alone"* (they used `catch { continue; }`, which never fired either):

```csharp
if (!MemberBool(unit, "IsLoaded", true)) continue;              // fallback returned
if (!TryMemberLong(br, "AxisIndex", out long axis)) continue;   // absence is a state you can branch on
```

**This does not replace the binding catalog.** The catalog is what makes a rename *loud* at startup; this stops a
call site from advertising a local defence it never had, so the two are not mistaken for one another.

Drilled on the day it was written (2026-08-23), and the drill paid immediately: the first version of the gate
caught the `catch { continue; }` form but **missed the headline dead-initializer form** — its regex could not
cross the `;` inside `GetMember(…);`. A planted violation exposed that in one run. The corrected gate then found
**ten more sites the hand review had missed**, all two-line declarations that a single-line grep never saw, and
one **false positive** — `Convert.ToInt32(GetMember(o, "Count") ?? -1)`, where the `??` supplies the fallback
before the convert, so that sentinel really is reachable. Excluded by name, because a gate that cries wolf on
correct code is a gate people learn to bypass. Tests: `Tests/MemberReadTests.cs`, including an **oracle** test
asserting `Convert.ToBoolean(null) == false` — the premise the whole bug class rests on.

### The binding-catalog surface guard (`tools/check-catalog.sh`)

`bindcheck` (below) validates every binding **in** the catalog, so its green light is a statement about the catalog —
not about the code. On 2026-08-21 a review measured the difference and found **84 member names read by name at
reflection call sites that were not catalogued**, several on functional paths behind silent catches
(`FacingAngleOffset`, `IdleAudioEvent`, `CurrentTechnologicalEraIndex`, `BonesCount`) — the CHANGELOG had claimed full
coverage on the strength of a *hand* sweep. This guard makes the claim mechanical: it extracts every string literal
passed to a by-name reflection accessor, subtracts the catalog, subtracts an allowlist where **every entry states its
reason** (Unity/BCL names; a handful of *tolerant probes* that try several names and cope with all absent), and fails
on the rest. Pure source analysis, so it runs in the fast gate. Fault-injected on the day it was written: dropping
`BonesCount` from the catalog, and adding a new uncatalogued site, were each caught by name.

Together the two are the whole claim: **covers the code** (this) **and resolves against the game** (bindcheck).

**Its blind spots are its real failure mode, and it has had three (2026-08-22, 08-23, 08-23).** This guard is a set of
regexes over source, so a call shape it does not match is not reported as unchecked — it is silently *not counted*, and
the pass line still says "all N catalogued" with a smaller N. Each time, the gate went green while the thing it exists
to catch sat in plain sight:

| found | shape it could not see | scale |
|---|---|---|
| 08-22 | `GetMember(GetMember(x, "Inner"), "Outer")` — pass 1 stopped at the first `)` | `TagAsAbilities`, read that way and only that way |
| 08-23 | the `CachedField` / `GFA` accessor family, newly added | 16 sites |
| 08-23 | `AccessTools.Field(x.GetType(), "name")` — pass 1's accessor list stopped at HAF's own helpers, and pass 2 gives up at the first `)` | **146 sites, 70 names** |

The third was not found by the gate, by `bindcheck`, or by review. It was found by a **log line**: `allMeshNames` missed
36 times in one session, and `typeprobe --exact` then said no assembly in the game declares that field at all — a dead
probe whose fallback branch rebuilt an array by reflection and discarded it. The gate had been reporting "all 331
catalogued" while never looking at the site. Drilled by putting a bogus member name in that shape: the shipped gate said
`OK — all 331`, the widened one failed.

The lesson generalises past this script: **a guard that filters before it counts cannot report its own blindness.** When
adding an accessor helper or a new call shape, add it to `extract()` in the same commit — and prefer a drill that injects
a bad name *in the new shape* over one that just re-runs the gate.

## Headless binding drift check (`Tools/check-bindings.sh` — for game updates)

A **different trigger** from the push gate: that guards HAF *code* changes; this guards *game* changes. After a Humankind
update, run:

> **How you learn a game update happened at all (2026-08-23).** Two signals, and the second closed a real blind spot.
> An update that **breaks** a binding was always loud — `HealthMissing > 0` puts a red banner at the top of the F8
> window naming exactly what broke. An update where every binding still **resolves** used to be *silent in-game*:
> `HealthSummary` was nulled, the banner is gated on `HealthMissing`, and the only trace was one log line and
> `haf_bindings_report.txt`. But resolution succeeding proves the *names* survived the update, not the *behaviour* —
> which is precisely the state where an update-caused oddity gets blamed on HAF. So an amber advisory now shows
> whenever `Application.version` differs from `GameBinding.VerifiedGameVersion`, naming both builds and pointing here.
> It is advisory by design (fail-soft) and silent on a verified build. The decision is a pure function
> (`VersionAdvisoryFor`) so it is unit-tested without a game, including the cases where it must say **nothing**: an
> unreadable version or an unpinned catalog are "no information", and a `?` in an advisory trains the reader to ignore
> the line. **`VerifiedGameVersion` is hand-updated** — bump it in the same commit as a re-verification, never before.

```
bash tools/check-bindings.sh [<…/Humankind_Data/Managed>]
```

The `bindcheck` tool (net8, `System.Reflection.MetadataLoadContext`) validates **every `GameBinding` catalog binding
against the build's assemblies without launching the game** — it reads `Patches/GameBinding.cs` directly (always in sync,
no manifest to stale) and inspects the game DLLs reflection-only (Unity's native deps don't matter). It prints
`bindcheck: N/N types | M member(s) missing` and exits non-zero on any drift, so a game patch's binding breakage is named
**headlessly** (CI-able on a version bump) instead of found by launching and reading `haf_bindings_report.txt`. It
evaluates the **derived** accessors too (`CachedDerived(... ElementType / FieldOrPropType / MethodParamType ...)`) along
the same chain the runtime walks — since 2026-08-21; before that it fell back to a bare-name lookup for them and
false-positived 7 of 12 on a clean build. Verified both ways: `91/91` clean on the pinned build, and it correctly flags
an injected fake binding — and, closing the catalog on 08-21, it caught five mis-attributions of mine before any launch
(`importAngles` on the wrong type, a member that exists on no assembly, three that live on runtime subclasses the
declared field type can't see). Its sibling **`tools/typeprobe`** (`dotnet typeprobe.dll <Managed> <Type>…`, or `--find <substring>` to list every
type/method/field/event whose name contains it — how the end-of-loading seam `LoadingScreen.VisibilityChanged` was
located for the load-tier smoke) dumps a game type's real field/property layout from the DLLs — it answered "why did `PawnFast` stay on reflection?"
(`HideFactor` is a packed property) and "where do a squadron's pawns live?" (`PresentationAirPatrolController`)
without a launch. It's the headless twin of
the in-game report — same catalog, no game needed.

## What it covers

| Function | Lives in | What's asserted |
|---|---|---|
| `ParseModels` | `UniversalInjectPatch.cs` | JSON→`ModelEntry` mapping via the generic `ToObject<ModelEntry>()`; omitted keys fall to the **shared `HafModelSchema` initializers** (`idleAltInterval` 25, `turretAxis` -1, `scale`/`brightness` 1); the **`position` Vector3** parses (Newtonsoft chokes on raw Vector3 — the strip-then-repin path is what's under test); signed GUID components; **per-object isolation** (an omitted field doesn't shift onto another model); **robustness** — garbage/empty input → empty *without throwing*; the regex fallback recovery when `JObject.Parse` rejects the document (keys entry count on `Min(pawnDescription, skel, atlas)`) |
| `ResolvePacks` | `UniversalInjectPatch.cs` | duplicate-modId reject (first file kept); `dependsOn`/`loadAfter` ordering; missing-dep skip + **transitive strand** (fixpoint); cycle → file-order + note; soft `loadAfter` to an absent modId; **stable seed order** (the invariant that keeps today's single-pack setup byte-identical) |
| `LongestMatch` | `UniversalInjectPatch.cs` | most-specific substring wins (not first-in-order); single-match fallback; no-match → null |
| `RegexStrArray` | `UniversalInjectPatch.cs` | wrapper string-array extraction; empty-item filtering; missing field → empty |
| `CoreDesc` | `UniversalInject.Combat.cs` | trailing `_NN` variant-suffix strip |
| `GuidToLong` | `UniversalInject.Combat.cs` | null / non-numeric → 0; numeric string parses |
| `EraFromName` | `UniversalInject.ScaleEra.cs` | extract `EraN` (case-insensitive, multi-digit); none/null → −1 |
| `EraAnchorFor` | `UniversalInject.ScaleEra.cs` | the Global Era Lab anchor rule — **a unit stays at 1.0 unless an authored grid cell says otherwise** (own-age-or-earlier → 1.0; later-but-unauthored → 1.0; non-positive eras clamp cleanly) |
| `GameBinding.Validate` / `Cached` | `Patches/GameBinding.cs` | the startup **reflection compatibility report** — resolves the catalog (~124 type + member bindings across the load-bearing injection path) incl. the simple-name (`Type.Name`) fallback scan, and writes a diffable `haf_bindings_report.txt` every launch; a game-update rename is *reported* (one `[MISSING]` line, headless-checkable), not silently absorbed. The report is self-validating: an added binding that isn't a real game member shows `[MISSING]` on the known-good build. |
| The four **live dials** | `Patches/DialConfig.cs` | `haf_rotortrim` / `haf_turnease` / `haf_hugterrain` / `haf_battleturn` — every known key, the shipped defaults (`lookahead` 3, `ease` 4, `cliff` 1 — not zero), the `air`→`hover` legacy alias, the **order-independent `hoverbank`→`bank` fallback**, the CSV name filters read *before* any numeric parse, CRLF, and one bad line never costing the rest of the file. Plus the reason the parse was extracted: every unrecognised line now yields a **named problem** (line number, the offending token, and the valid keys) instead of being silently dropped. See below. |
| The **per-frame pose decisions** | `Patches/PoseMath.cs` | which clip a pawn plays and where in it — the thing the player actually sees. The **proximity-weighted state vote** (`PickState`) and why it is not a headcount or a nearest-pick; the representative coming from the *winning* side; the attack window (first match, not nearest) and its unclamped `repeats` passes; the after-move / pre-move one-shots and the **never-quite-1.0 clamp** that stops a held frame wrapping to the folded pose; the nearest-fire match; the deploy ramp; the recoil sweep. And the invariant a tidy-up would break: **the three match radii differ** (state 4u, fire 4u, deploy 3u). |
| `MergeModels` | `UniversalInjectPatch.cs` | the pack **merge policy**: first-loaded keeps an undeclared clash (conflict), a declared override replaces in place, and `disabled` is honoured on every path — a disabled declared override leaves the owner in place with a named note |
| `PackTuning.Parse` | `Patches/UniversalInject.PackTuning.cs` | the three pack tuning tables (`unitScales` / `eraGrid` / `formationThresholds`) parsed from the **resolved** packs in mod order — a skipped pack contributes nothing, later-in-mod-order (not alphabetical) wins a row, and every cross-pack interaction is a named note |
| **Thread-discipline rule** | `Patches/ThreadDiscipline.cs` | **a structural test**: every mutable field on `ModelEntry` must declare `[MainThread]` / `[Locked]` / `[Concurrent]`, `[Concurrent]` is machine-checked against the field's real type (both directions), the four Architecture §2 locked fields are pinned against silent demotion, and the inherited `Haf.Schema` half must stay free of mutable collections so "config is immutable" holds by construction. Replaces the memorised four-name table; found 17 of 23 mutable collections declaring nothing |
| **Session-state rule** | `Patches/SessionState.cs` | **a structural test**: reflects over every static collection field in the plugin and fails unless each declares `[SessionScoped]` / `[SessionScoped(Manual=…)]` / `[ProcessLived(…)]`; plus the registry really clears only the registered fields of the asked scope (a fixture holder in the test assembly). This is the "every session-keyed static gets cleared on re-arm" invariant of Architecture.md §3 as code — the bug class behind the Oracle incident, the `_DRILL` pack-data bug and the tank-destroyer donor skin |
| `SmokeVerdict` | `Patches/UniversalInject.SmokeTest.cs` | the **in-game smoke harness's** PASS/FAIL rule (injection errors = a per-session ledger of named sites, once each — `500 frames of one throwing model = 1 error, named`). **Live-pawn truth** (2026-08-21): `GatherLivePawnFacts` is pure over `(descId, skeletonId)` slots + entries + the clock — a live pawn on a foreign skeleton (rendering the donor), an entry with live pawns the pose hook hasn't touched in 5 s, and a sub-pawn the scene scan sees but the walk misses are each a named FAIL; the runtime side only collects the slots — PASS iff every catalogued binding resolved, zero injection errors, the registry loaded ≥1 model, the deep per-entry checks are clean, and the live **seam write-back self-test** did not FAIL (the boxed-struct chain every runtime offset uses — the combatZ died-in-the-box class, machine-caught since 2026-08-19); each fail reason surfaced; `repointed`-zero still passes but is NOTED (vacuous coverage announces itself), uninjected entries are named with a diagnosis, and the verdict is written to `haf_smoke_report.txt` next to the load/bindings reports. **Districts (2026-08-21):** live tiles are counted from whichever ledger owns the district (isolate `DistrictModel.tiles`, scoped `ScopedState.refreshPlbcs` — the `, N scoped` suffix), and **texture health** is judged from a pure `DistrictTexState` with `texErrors` read FIRST, because both apply paths give up after 3 exceptions by latching `texApplied=true`: gave-up → FAIL (named), applied → `N/M textured`, pending → NOTE, no atlas or no live tile → not judged |

These map directly to the registry bugs this codebase has actually hit — the `ParseGuidCsv` sign bug, `LongestMatch`
ambiguity, "wrapper-parse drops overrides", the substring pawn-match — so the suite is a **regression net, not
coverage theatre**.

## Extracting logic so it *can* be tested

Most of the plugin cannot be unit-tested: it is reflection against a live game inside Unity. But the *decisions*
buried in that code usually can be, once they are lifted out of the method that does the I/O. `SmokeVerdict` was the
first extraction of this shape; **`DialConfig`** (2026-08-20) is the second, and the pattern is now the standard move:

> Find a method that mixes I/O, engine access and a **decision**. Move the decision to a pure static that takes
> plain data and returns plain data. Leave the I/O where it is. Test the pure half.

The dials are the clearest case. Four `haf_*.txt` files each inlined their own `key=value` loop inside a `Poll*`
method, wedged between `File.ReadAllText`, `UnityEngine.Time` and live-pawn reflection — untestable, and all four
shared one failure: **any line the parser did not understand was `continue`d away in silence.** `radus=6`,
`hoverbanks=12`, a European `rate=1,5` — each produced a working plugin that quietly ignored the setting, with
nothing in the log. That is the "silently disarmed" class [the 07-31 audit](notes/Audit-2026-07-31.md) was written
about, sitting in the one part of HAF a user hand-edits mid-session.

The parse is now `Patches/DialConfig.cs`: text in, typed config + a list of problems out. The `Poll*` methods keep
the I/O and log whatever problems come back, so a typo now names its own line number.

### Guarding a refactor of shipped behaviour

Extracting live code risks changing it. Tests written *after* the extraction only pin what the code does now — they
would pass just as happily over a subtly wrong parser. So two extra things were done, and both are worth repeating on
the next extraction:

1. **A legacy parity oracle** (`Tests/DialLegacyParityTests.cs`). The original inline loops are kept verbatim as
   oracles and compared against the new parser over a 39-case corpus — valid input, half-typed input, CRLF, comma
   decimals, repeated keys, stray `@`. Values must match exactly; diagnostics are excluded, since emitting them is
   the point of the change. It found and documents the **one** deliberate divergence: a line like `@1=5` used to
   produce a trim with an empty bone name, and since `name.IndexOf("")` is `0` for every string, that silently
   rotated the **first bone in the skeleton**. It is now dropped with a message.
2. **A mutation drill.** Six mutations were planted in the parser and the suite re-run. Five behaviour-changing ones
   were each caught (dropping the `hoverbank` fallback → 5 failures; `only`/`skip` falling through to the numeric
   parse → 6; `lookahead` default 3→0 → 39; re-silencing unknown keys → 4; re-accepting an empty bone name → 4;
   dropping malformed-line reporting → 4). The sixth — resolving the `hoverbank` fallback inline rather than after
   the file — passed, correctly: it is a genuinely equivalent implementation, not a defect. A mutation that does not
   fail the suite is either a gap or an equivalence, and you have to tell which; assuming "gap" would have added a
   test asserting an implementation detail.

3. **An in-game drill**, because the two above are still only the suite grading itself. Six deliberately broken
   lines were planted across the live `haf_*.txt` dials — an unknown key, a comma decimal, a line with no `=`, a
   line with two, a transposed key, and a bone-less `@1=5` — each chosen to be provably **value-neutral**, so the
   dials had to keep working while every fault got named. The log showed all six warnings with correct line
   numbers, values byte-identical to the pre-change run, `reloaded 0 line(s)` for the `@1=5`, and — the negative
   control that matters — **zero warnings once the faults were removed**, proving they fire on faults rather than
   on every poll.

   **And the drill found a bug all 323 green tests had missed.** The `[Hug]`/`[TurnEase]` echo lines used plain
   string interpolation, so on a comma-decimal machine the log printed `lookahead=1,5` — the exact spelling the
   parser rejects, one line above the new warning saying *use '.' for the decimal point*. Copy a value out of the
   log back into the file and it silently dies. Fixed with `DialConfig.Inv()` and pinned by a round-trip property
   — *whatever the log prints must parse straight back* — asserted under `nl-NL`.

The rule this follows is the project's own: [review, then drill](notes/Audit-2026-07-31.md). A suite that has never
been shown to fail is not yet evidence of anything — and a suite that has never been checked against a real machine
is not yet evidence of much either. The unit tests could not have found the locale bug: they *are* the code's
opinion of itself, and both halves shared the same blind spot.

### The second extraction, and what it taught about the guard rails (`PoseMath`, 2026-08-20)

The per-frame pose decisions went the same way — `PickState`, the attack/after/pre-move windows, the nearest-fire
match, the deploy ramp and the recoil sweep, out of `StatePose`/`DeployPoseTime`/`FireOncePoseTime` and into the pure
`Patches/PoseMath.cs`. Two findings worth carrying forward:

**The oracle earns its keep on transcription, not on algorithms.** Reading the two nearest-fire call sites had
convinced me they were the same loop written twice. They are not: the recoil overlay seeded `best` with the radius
(strictly inside), fire-once seeded with `float.MaxValue` and range-checked afterwards (inclusive), so they disagree
for a fire at a distance of **exactly 4.0**. The corpus found it in seconds. Unified to strictly-inside — matching
what the other two matchers already do — and recorded as the one deliberate behaviour change, with a named test.

**A random corpus is the wrong instrument for an algorithm choice.** The mutation drill replaced `PickState`'s
proximity weight with a constant (turning the vote into a headcount) and the oracle sailed straight past thousands
of generated layouts. That is not a corpus-tuning problem: the two rules only disagree on small *unbalanced*
in-range splits, and as the sample count rises the two majorities converge, so a **bigger** corpus fires **less**
often. Widening the draw and enlarging the formations both failed to catch it; only an adversarial hand-written case
does (one sample at the pawn's feet against two at the radius edge). Two tools, two jobs — a generated corpus pins
that the code was *copied* faithfully, hand-written adversarial cases pin that it *decides* the right thing. Neither
substitutes for the other, and a mutation drill is how you find out which one you are missing.

## How it's wired

- **Framework:** xUnit, `net471` (matches the plugin), one test project `Tests/HumankindAssetFramework.Tests.csproj`.
- **Access:** the tested helpers are `internal`, exposed to the test assembly via `[InternalsVisibleTo]`
  (`Properties/AssemblyInfo.cs`). A few were bumped `private→internal` purely for this; none were made `public`.
- **`Plugin.Log`:** null outside the game, so each test class's ctor sets `Plugin.Log = new ManualLogSource("test")`
  (a listener-less source → every `LogXxx` is a safe no-op).
- **Dependencies:** needs the same gitignored `References\` DLLs as the plugin build; the test project mirrors them
  into its own bin so the plugin assembly's deps resolve at runtime. `Tests\**` is excluded from the plugin's compile
  globs so the xUnit files never leak into the plugin build.

## What is deliberately NOT unit-tested — and why

This boundary is intentional. Adding tests past it would be green ceremony that guards nothing real.

- **The runtime/integration seam** — inject, pose, muzzle, audio, districts, formations. These reflect into Amplitude
  types that only exist inside the running game process; they can't be loaded in a test host, and a fake object model
  under the reflection accessors was considered and declined (reflection is not funnelled — ~1,450 sites — and a fake
  encodes the very assumptions about the game that drills keep disproving; see Decisions.md). Their correctness comes
  from **fail-soft resilience** (per-entry try/catch, null-guards), the editor-side bake smoke/feature tests, and the
  **in-game smoke test**, which is the *right* instrument for this half because it reads the engine, not a model of it:
  - the **load tier** runs by itself once per session, on the first frame after the loading screen hides
    (`Amplitude.Mercury.LoadingScreen.VisibilityChanged`, `SmokeOnLoad = true`) — bindings, registry, clip roles,
    assets, sounds, files on disk, GPU budget, district tiles + textures, patched seams. A few ms, at a moment the
    player is already waiting; never per frame. Tagged `[load]`.
  - the **full tier** is the F8 button — load + the **live-pawn checks**: every live pawn slot carrying one of our
    descriptor ids sits on *our* skeleton (a unit rendering its donor is a named FAIL), the pose hook touched every
    entry with live pawns within 5 s, the sub-pawn walk re-audited against a full scene scan, the ObjectSpace
    write-back self-test. Needs pawns on the map and a few hook frames, hence not at load. Tagged `[full]`.
  - both write the log, the F8 panel and `haf_smoke_report.txt`; the **verdict and every classifier are pure and
    unit-tested** (`SmokeVerdict`, `GatherEntryFacts`, `GatherLivePawnFacts`, `UninjectedReason`, …) — only the
    gathering of live numbers via reflection runs in-game. Each check was earned by a shipped bug class, and each
    first in-game run has so far found a gate the check needed (retexture-only entries have no skeleton; the
    skeleton check once fired on one, and so did the live-pawn check on its first run).
  - the **rebuild → relaunch → read the log** drill is still the final word for *visual* truth (does the helicopter
    follow the terrain) — the smoke proves the engine state, not what it looks like.
- **`ParseGuidCsv`, `MakeGuid`, `EmitterName`** — build/consume Amplitude types via reflection, absent in the test host.
- **`FindEntryForUnitDefinition`** — delegates to the already-tested `LongestMatch` + `CoreDesc`; testing it would mean
  exposing the `entries` global as a test seam for ~zero new coverage.
- **Non-collection session statics** (`bool`/`int` latches like `registered`, `cachedEra`) — outside the
  `SessionStateTests` rule, which covers every static *collection*; they stay on the hand-list in
  `RearmModelRegistration`, and the registry cannot prove reset *order* either (Architecture.md §3).
- **Trivia** (`StrList`, `SanitizeFile`, one-line accessors) — too trivial to regress meaningfully.

## Adding a test

1. If the target is `private`, bump it to `internal` (never `public` just for tests) — `[InternalsVisibleTo]` handles
   the rest.
2. Only test **pure** logic (string/JSON/data in → data out). If it reflects into Amplitude/Unity, it belongs in the
   in-game smoke test, not here — and the pattern there is the same: extract the *decision* into a pure function that
   takes plain values (`GatherLivePawnFacts` takes `(descId, skeletonId)` slots, not pawns), test that, and keep the
   reflection side to a thin collector.
3. A new **smoke check** goes in the tier it can be true in: load tier if it needs only the loaded world, full tier if
   it needs pawns on the map. Gate it on what the entry *authored* (a retexture-only entry has no skeleton) and give
   it a unit test against `SmokeVerdict` before the first in-game run.
4. Set `Plugin.Log` in the fixture if the code under test logs.
5. Prefer tests that pin a **real invariant or a historic bug**, not line coverage.

See also: `docs/Building.md` (build/run), `docs/Code-Map.md` (where the tested functions live),
`docs/Framework-Review.md` (the dated changelog of what each test batch added).
