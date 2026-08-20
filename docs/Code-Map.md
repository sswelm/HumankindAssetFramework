# Code map — the HAF runtime plugin

Where things live in the plugin (`C:\Repo\HumankindAssetFramework`, the BepInEx runtime half). The Unity editor/baker
tooling is a separate project (`C:\Repo\ENCReload\Assets\Scripts\Editor\`); this map is the runtime only.

## Top level

| File | Role |
|---|---|
| `Plugin.cs` | BepInEx entry point. Config binds, the Harmony hook **registration list** (every `Hk_*` / `Uni*Hook` must be listed here or it silently never patches), the per-frame `Update()` pump, and the F8 diagnostic window. |
| `Prober.cs` | Standalone reflection prober / dev spelunking. |
| `Haf.Schema/HafModelSchema.cs` | The **shared model schema** (netstandard2.0 DLL): the fields stored identically by the editor's `ModelDef` and the plugin's `ModelEntry`, defined ONCE. Both classes **inherit** it (de-duplicated, no hot-path churn); ships next to the plugin (`ProjectReference`) — deploy both via `tools/deploy-plugin.sh`. GUID/bake/runtime-state fields stay on the two classes (divergent by design). |
| `Patches/GameBinding.cs` | Startup compatibility report + the one place each game-type NAME lives (call sites bind via `GameBinding.<Type>`, not a scattered `Type.GetType`). Resolves a catalog of the game types/members HAF binds to (59 types / ~160 members — including the hot-path structs, DERIVED from anchor members rather than named, so no false positives), logs `[GameBinding] OK` or a specific `NOT FOUND`, **and** writes a machine-readable `haf_bindings_report.txt` (next to `haf_load_report.txt`) every launch — a diffable `[ok]`/`[MISSING TYPE]`/`[MISSING MEMBER]` line per binding, so a game-update rename surfaces as one report line (headless-checkable) instead of a feature silently misbehaving. Plain `System.Reflection`, unit-tested. |
| `Patches/DialConfig.cs` | The **pure parse half of the four live `haf_*.txt` dials** (rotor trim, turn ease, terrain hug, battle turn). No Unity, no file I/O, no reflection: text in, typed config + a list of human-readable problems out. The `Poll*` methods keep the I/O and log the problems, so an unrecognised line names its own line number instead of being silently dropped. Unit-tested (`Tests/DialParseTests.cs`) with a legacy-parity oracle (`Tests/DialLegacyParityTests.cs`). Adding a dial key = one `case` + one word in that dial's `Known` list. |
| `Patches/PoseMath.cs` | The **pure per-frame pose decisions**: which clip a pawn plays and where in it. The proximity-weighted state vote (`PickState`), the attack/after/pre-move windows, the nearest-fire match, the deploy ramp and the recoil sweep. No reflection, no `Time.time`, no locking — the `Poll`/`Pose` callers still take the lock and pass the list in. **The three match radii are NOT the same** (state 4u, fire 4u, deploy 3u); read the constants before touching a call site. Unit-tested (`Tests/PoseMathTests.cs`) with a legacy-parity oracle (`Tests/PoseMathLegacyParityTests.cs`). |
| `Patches/` | The injection engine + Harmony patches (below). |
| `Tests/` | xUnit suite over the pure registry/parse/era layer. See `docs/Testing.md`. |
| `baker/` | LIVE pipeline pieces that live ONLY here: `glbconv/` (the GLB→OBJ converter — single source of truth) and `reactor_silhouette.py`. The editor-script snapshot that used to sit alongside them was deleted 2026-08-21 (see `baker/README.md`); the authoritative editor tooling is in ENCReload. |

## The injection engine — `UniversalInject` (one `partial class`, split by concern)

`UniversalInject` is a single `internal static partial class` spread across files (Phase-1 split, 2026-08-02).
All partials share one field set and one member namespace — the file boundaries are for navigation only, they
carry no access or lifetime meaning. To find a method, pick the concern:

| File | What's in it |
|---|---|
| `UniversalInjectPatch.cs` | The "home" file: registry load/parse/resolve/conflict-merge (`LoadRegistry`, `ParsePack`, `ParseModels`, `ResolvePacks`), unit↔entry matching (`LongestMatch`, `FindEntryForUnitDefinition`), `RearmModelRegistration`. Also the **standalone types**: `ModelEntry`, `StateSample`, `FireInstance`, `DeploySample`. |
| `UniversalInject.Inject.cs` | Skeleton registration + repoint onto our mesh (`EnsureRegistered`, `RepointMatch`, `EnsureUploaded`), body-mesh discovery/rename, `ReloadFragments`, `InjectHandProp`, and the donor-dump diagnostics. |
| `UniversalInject.Retexture.cs` | Texture-only reskin (keep vanilla mesh): `ApplyTexture`, `ApplyTextureOnly`, `GreyIsolate` (private output-layer clone so the reskin can't bleed onto the emblematic original). |
| `UniversalInject.Clips.cs` | Animated-clip plumbing: `LoadClipCollection`, `InjectClipCollections`, `ResolveAnimId` / `ResolveCollAnimId`. |
| `UniversalInject.ScaleEra.cs` | Unit size + era aging: `CurrentEra`/era resolution, `ScaleDescriptorMeshes` (the mesh-scale engine), formation-swap-by-size, `ResizeStatusLines`. |
| `UniversalInject.Pose.cs` | Pawn creation + per-frame pose: `OnPawnAdded`, `ForceOurSkeleton`, `ApplyFreeze`, `ApplyAnimatedPose`, `PhaseFor`, `ComputePoseTime`, `StatePose` (idle/move/after/attack state machine), `DeployPoseTime`, `FireOncePoseTime`. Nested `PawnCtx`. |
| `UniversalInject.Muzzle.cs` | Turret aim + muzzle: `TurretizeAimLayer`, `MuzzleRedirect`/`CompensateDonorOffset`, `SanitizeAimLayer`/`ClearAimLayer`, `ApplyPositionOffset`, `ApplyScale`, `LogPoseHookOnce`. |
| `UniversalInject.Combat.cs` | Combat + post-load: `MaybeRespawnPostLoad` (first-instance rotor race), `ProcessFireQueues`, `TryEarlyAttackSound` (the FaceEnemy roar seam), `OnPawnDeath`/`OnBattleStarted`/`ProcessBattleCries`, `ProcessAnimStates`, `ProcessDeployState`, `TickOne`. |
| `UniversalInject.Audio.cs` | Engine/move audio: `ProcessEngineAudio`, emitter helpers, `PlaySoundTest`, `DumpSoundCatalog`. Also the Game Sound Lab plumbing: `ShouldSilenceEvent`/`EnsureSoundOverrides` (silence vanilla events from `haf_sounds.json`) and `PlayEventByName`/`StopEventAudition` (F8 audition). See `docs/Game-Sound-Lab.md`. |
| `UniversalInject.Districts.cs` | District-visual repoint, prop/projectile registration, `DumpMeshBudget`, `ParseGuidCsv`. Nested `DistrictModel`. |
| `UniversalInject.Reflection.cs` | The one member reader/writer the whole plugin funnels through: `GetMember`/`SetMember`/`MakeGuid` + the `(type,name)` member cache. |
| `UniversalInject.SmokeTest.cs` | The in-game smoke harness: `RunSmokeTest` (gathers live binding/registry/injection counts and logs a single PASS/FAIL line, echoed to the F8 panel), the pure **`SmokeVerdict`** (unit-tested), and the `InjectionErrors` counter the four injection paths bump. See `docs/Testing.md`. |
| `UniversalInject.Hooks.cs` | The Harmony patch classes that call into the above: `UniRegisterHook`, `UniRepointHook`, `UniPawnPoseHook`, `Hk_MuzzleRelocate`, `Hk_AudioTrace`, `Hk_DistrictRepoint`, `Hk_AnimatedBonePoolHeadroom`, `Hk_DistrictBufferHeadroom`, `Hk_PropRegister`, `Hk_ProjectileOverride`. |

Other patch files (already separate, not part of `UniversalInject`):

| File | Role |
|---|---|
| `Patches/CombatEventPatch.cs` | Sim/presentation combat hooks: `Hk_EarlyAttackSound`, death cue, battle-start, VFX suppression, projectile stash, `FireProbe`. |
| `Patches/FormationOverridePatch.cs` | Formation axis: pawn-count + layout override, prefab/instance pool extension. |
| `Patches/FacingPersistPatch.cs` | Save/load facing restore (the standard save has no facing field). |

## Reflection

All reflection member access funnels through **`UniversalInject.GetMember`/`SetMember`** (cached, property-first,
finds non-public), which live — with `MakeGuid` and the member cache — in `UniversalInject.Reflection.cs`.
`FormationOverride.Mem` and `FireProbe.Member` are thin forwarding aliases to `GetMember`. (`FacingPersist` keeps
its own small field-only cache, `CachedField`, for its self-contained use.)

## Conventions

- **A new Harmony hook only fires if it's added to the `hooks[]` list in `Plugin.cs`** — a missed registration
  is silent. The load line reports `patched/total` counting the methods actually patched (not "didn't throw").
- Adding a **registry field**: the fields stored identically by both classes are the **shared
  `Haf.Schema.HafModelSchema`** — add it there ONCE (default = the field initializer) and both `ModelDef` (editor) and
  `ModelEntry` (plugin) inherit it (compiler-enforced, can't drift). The plugin's primary parse is **generic**
  (`m.ToObject<ModelEntry>()` in `ParseModels`), so a name-matching field maps automatically; only the GUID arrays
  (`skel[]` → `sa/sb/..`) and `position` (Vector3 — Newtonsoft chokes on it, stripped + re-pinned by hand) are explicit.
  What still hand-syncs: the **regex fallback** (malformed-JSON path) hand-lists every field — add the matching
  `Regex.Matches` line there. **Then run the drift guard:** `bash ../ENCReload/Tools/check_schema_parity.sh` — it fails
  loudly if the fallback lags the shared schema or the GUID hand-lists, or reads a key the baker never writes.
- `ModelEntry` **inherits** `Haf.Schema.HafModelSchema` for the shared fields (de-duplicated via inheritance, no
  hot-path churn); the POCO *decomposition* split is still declined. Keep runtime-state / divergent-GUID fields on
  `ModelEntry` itself.
