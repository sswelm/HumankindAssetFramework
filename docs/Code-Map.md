# Code map — the HAF runtime plugin

Where things live in the plugin (`C:\Repo\ENCAccessProof`, the BepInEx runtime half). The Unity editor/baker
tooling is a separate project (`C:\Repo\ENCReload\Assets\Scripts\Editor\`); this map is the runtime only.

## Top level

| File | Role |
|---|---|
| `Plugin.cs` | BepInEx entry point. Config binds, the Harmony hook **registration list** (every `Hk_*` / `Uni*Hook` must be listed here or it silently never patches), the per-frame `Update()` pump, and the F8 diagnostic window. |
| `Prober.cs` | Standalone reflection prober / dev spelunking. |
| `Patches/GameBinding.cs` | Startup compatibility report: resolves a catalog of the game types/members HAF binds to and logs `[GameBinding] OK` or a specific `NOT FOUND` warning (makes reflection drift loud). Plain `System.Reflection`, unit-tested. |
| `Patches/` | The injection engine + Harmony patches (below). |
| `Tests/` | xUnit suite over the pure registry/parse/era layer. See `docs/Testing.md`. |
| `baker/` | STALE editor snapshot (do not edit/bake here) + LIVE `glbconv`/`Tools`. See `baker/README.md`. |

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
- Adding a **registry field** touches FOUR hand-synced places: the editor `ModelDef` (`ENCReload/…/ModelRegistry.cs`),
  `ModelEntry`, and the plugin's two read paths — the Newtonsoft object parse **and** the regex fallback, both in
  `ParseModels` (`UniversalInjectPatch.cs`). Miss one and the feature silently dies with no error. **After the edit, run
  the drift guard:** `bash ../ENCReload/Tools/check_schema_parity.sh` — it asserts Newtonsoft == regex keys, every read
  key is a `ModelDef` field, and the cast types agree. This is the schema single-source-of-truth mechanism (a full
  auto-bound-POCO merge was deliberately declined; the guard monitors the duplication instead).
- `ModelEntry` is a deliberately flat data object (the POCO split was declined); leave it as one type.
