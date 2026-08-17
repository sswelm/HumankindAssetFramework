# Decisions

Short records of **settled decisions** — the *why* behind choices that would otherwise be re-litigated, or
reverse-engineered from the code months later. Each entry is the decision, the reasoning, and (where useful) what was
rejected. Newest first. This is the fast answer to "why is it like this?" without archaeology — and the first place to
check before proposing a change to any of them.

---

## glbconv has ONE source: `baker/glbconv/` in the plugin repo (2026-08-17)
The converter's source, csproj, and pinned `SharpGLTF.Core.dll` live ONLY in this repo; ENCReload holds just the
deployed `glbconv.exe` + BUILD.md. **Why:** the previous two-copies arrangement split-brained — each copy accumulated
a verified fix the other lacked, and the 2026-08-16 rebuild shipped the deployed exe with the T5 mirrored-winding fix
silently regressed. A cross-repo file copy without a sync guard eventually ships a regression. **Rule:** never
reintroduce a second source copy; after any rebuild, A/B-diff old-vs-new OBJ output before deploying (procedure in
ENCReload `Tools/glbconv/BUILD.md`).

## A cross-repo copy is either authoritative or it doesn't exist (2026-08-01 / 2026-08-17)
The editor `.cs` files in `baker/` are a **deliberately stale reference snapshot** (2026-08-01: entangled with the
live glbconv + csproj packaging note, so documented loudly in `baker/README.md` instead of deleted); the
authoritative, runnable copies live in ENCReload (`Assets/Scripts/Editor/`, git-tracked). The Blender-script copies
(`baker/*.py` + `baker/Tools/`) got the opposite treatment on 2026-08-17 — **deleted** — because unlike the `.cs`
snapshot they were labelled "live" while the pipeline never executed them (it resolves `<UnityProjectRoot>/Tools/`):
a drift trap, not documentation. **Rule:** "stale but documented" is reserved for the `.cs` snapshot alone; every
other file has exactly one home.

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

## No `ModelEntry` split / POCO refactor (declined)
The plugin's `ModelEntry` is a large struct; a proposed split into smaller POCOs was **declined**. **Why:** the churn
touches the settled ownership model and the reflection-cached per-frame hot path for no proven bug; the drift it was
meant to prevent is already handled by the fail-safe rebase + the schema-parity guard. Don't resurface.

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

## A focused unit suite, not broad coverage or an in-game test framework (settled)
The plugin has a bounded suite (**90 tests**) over the **pure logic that runs outside the game** (parse / schema /
reflection-resolution / the smoke-verdict rule); everything engine-coupled is verified by in-editor instruments (Feature
Test, smoke test) plus in-game. **Why:** the game can't run in a test host, so a coverage *target* over engine-coupled
code would be theatre — the suite guards where bugs have actually hidden and stops there. Broad automated testing is
revisited at the public-package push. See [Testing.md](Testing.md).
