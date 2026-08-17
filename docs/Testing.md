# Testing

The plugin has a focused unit-test suite (**89 tests as of 2026-08-17**) over the **pure logic that can run outside the
game** — the registry/parse/era layer, the reflection **compatibility report** (`GameBinding`), and the **in-game smoke
harness's verdict** (`SmokeVerdict`). It is a deliberate, bounded suite, not a coverage target: it guards the functions
where bugs have actually hidden, and stops there on purpose.

```
dotnet test Tests/HumankindAssetFramework.Tests.csproj -c Release
```

## The pre-push gate (`Tools/check.sh`)

The fast guards used to be separate scripts you had to remember to run. They're now one command per repo, wired as a
**pre-push hook** so a push can't land a broken build, a failing test, or a drifted schema:

| Repo | the `check.sh` gate runs | ~time |
|---|---|---|
| **HumankindAssetFramework** (plugin) | `dotnet build` · `dotnet test` (89) · registry schema parity | seconds |
| **ENCReload** (editor) | Roslyn editor compile-check · registry schema parity | ~30 s |

Run it any time by hand: `bash tools/check.sh`. **Enable the hook once per clone:**

```
git config core.hooksPath tools/git-hooks
```

(Casing matters on case-sensitive filesystems: this repo's folder is lowercase `tools/`; ENCReload's is `Tools/` —
the mismatch has already eaten files once, commit `db40e73`.) The hook (`tools/git-hooks/pre-push`,
version-controlled) then blocks a failing push; bypass only in a real emergency
with `git push --no-verify`. Deliberately **not** in the gate (too slow / need Unity, Blender, or the game): the Blender
golden-master `deploy_regression.sh`, the in-editor Feature Test, and the in-game binding report — those stay manual. The
gate earned its keep on day one: standing it up surfaced three latent schema drifts (a wrapper field the plugin read but
the baker never wrote, two runtime-only keys, and a `float?`-cast the parity script mis-classified), all fixed to green.

## Headless binding drift check (`Tools/check-bindings.sh` — for game updates)

A **different trigger** from the push gate: that guards HAF *code* changes; this guards *game* changes. After a Humankind
update, run:

```
bash tools/check-bindings.sh [<…/Humankind_Data/Managed>]
```

The `bindcheck` tool (net8, `System.Reflection.MetadataLoadContext`) validates **every `GameBinding` catalog binding
against the build's assemblies without launching the game** — it reads `Patches/GameBinding.cs` directly (always in sync,
no manifest to stale) and inspects the game DLLs reflection-only (Unity's native deps don't matter). It prints
`bindcheck: N/N types | M member(s) missing` and exits non-zero on any drift, so a game patch's binding breakage is named
**headlessly** (CI-able on a version bump) instead of found by launching and reading `haf_bindings_report.txt`. Verified
both ways: `49/49` clean on the pinned build, and it correctly flags an injected fake binding. It's the headless twin of
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
| `SmokeVerdict` | `Patches/UniversalInject.SmokeTest.cs` | the **in-game smoke harness's** PASS/FAIL rule — PASS iff every catalogued binding resolved, zero injection errors, and the registry loaded ≥1 model; each fail reason surfaced; `repointed`-zero still passes (no units on the map isn't a failure) |

These map directly to the registry bugs this codebase has actually hit — the `ParseGuidCsv` sign bug, `LongestMatch`
ambiguity, "wrapper-parse drops overrides", the substring pawn-match — so the suite is a **regression net, not
coverage theatre**.

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
  types that only exist inside the running game process; they can't be loaded in a test host. Their correctness comes
  from **fail-soft resilience** (per-entry try/catch, null-guards), the **rebuild → relaunch → verify-log** discipline,
  the editor-side bake smoke/feature tests, and the **in-game smoke harness** — an F8-triggered runtime integration
  check (`RunSmokeTest`) that asserts the plugin came up and injected cleanly and logs one PASS/FAIL line. That's the
  *right* instrument for this half: a human loads a game, the harness does the checking. Its **verdict logic is pure
  and unit-tested** (`SmokeVerdict`, above); only the genuinely untestable part — gathering the live numbers via
  reflection — runs in-game.
- **`ParseGuidCsv`, `MakeGuid`, `EmitterName`** — build/consume Amplitude types via reflection, absent in the test host.
- **`FindEntryForUnitDefinition`** — delegates to the already-tested `LongestMatch` + `CoreDesc`; testing it would mean
  exposing the `entries` global as a test seam for ~zero new coverage.
- **Trivia** (`StrList`, `SanitizeFile`, one-line accessors) — too trivial to regress meaningfully.

## Adding a test

1. If the target is `private`, bump it to `internal` (never `public` just for tests) — `[InternalsVisibleTo]` handles
   the rest.
2. Only test **pure** logic (string/JSON/data in → data out). If it reflects into Amplitude/Unity, it belongs to the
   Phase-5 in-game seam, not here.
3. Set `Plugin.Log` in the fixture if the code under test logs.
4. Prefer tests that pin a **real invariant or a historic bug**, not line coverage.

See also: `docs/Building.md` (build/run), `docs/Code-Map.md` (where the tested functions live),
`docs/Framework-Review.md` (the dated changelog of what each test batch added).
