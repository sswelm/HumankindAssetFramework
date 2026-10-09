# Ecosystem Survey — what other Humankind BepInEx plugins solve, and what we can learn

> **📁 ARCHIVED NOTE — frozen 2026-07-12, not maintained.** A point-in-time survey of other Humankind plugins against
> 2021–2024 game versions. Re-verify every class/method name against current game DLLs before relying on one.
>
> **Addendum 2026-10-10:** one entry added, [lcaresia/realpolitik](#the-2026-arrival-lcaresiarealpolitik) (first
> published 2026-10-06, read at commit `cc059a5`). It is the only entry written against a current game build
> (HUMANKIND 1.31.4836). The twelve original entries are unchanged.

*Compiled 2026-07-12 from a GitHub-wide search (repo/code/README search + walking each known modder's
account), then reading each repo's README and key sources. Everything below was verified against the
actual code, not just descriptions. Class/method names are as they appear in those repos (2021–2024
game versions) — re-verify signatures against current game DLLs (ilspycmd) before relying on one.*

**Why this document:** these repos collectively map which Amplitude systems are patchable and how the
community solved problems we share (lifecycle timing, per-entity runtime state, sim→presentation
sequencing, distribution). Use it as a technique index when building the next Factory feature.

## Quick reference

| Repo | Problem it solves | Headline technique for us | License | Last activity |
|---|---|---|---|---|
| [Theadd/Humankind-GUI-Tools](https://github.com/Theadd/Humankind-GUI-Tools) | In-game toolbox: unit spawner, cheats, live map editor, free cam | `PostAndTrackOrder(...).UponCompletion(...)` + `PresentationArmy.DoUpdateMesh` | MIT | 2024-10 |
| [Theadd/Modding.Humankind.DevTools](https://github.com/Theadd/Modding.Humankind.DevTools) | Framework library: game-state facades, lifecycle, hotkeys, window kit | Readiness gates; Interop-vs-Simulation dual access map | MIT | 2022-11 |
| [Theadd/HumankindHacks](https://github.com/Theadd/HumankindHacks) | Cheat/QoL plugins + shared-project framework v2 | `AssetHunter` (find which loaded bundle holds an asset); zero-Harmony coroutine architecture | none | 2022-06 |
| [Theadd/ModdingTools](https://github.com/Theadd/ModdingTools) | CLI that scaffolds a BepInEx plugin workspace | Assembly publicizer + wildcard-reference build props; hot-reload loop | none | 2023-01 |
| [Gedemon/HK-CultureUnlockFromTerritory](https://github.com/Gedemon/HK-CultureUnlockFromTerritory) | Cultures unlock only if you hold their historical territory | `Sandbox.ThreadStart/ThreadStarted` lifecycle trio; `Databases.GetDatabase<T>().Touch(row)` | none | 2022-10 |
| [Gedemon/HK-Uchronia](https://github.com/Gedemon/HK-Uchronia) | TCL successor + battle postures, AI faction choice, debug overlay | Per-entity save extension keyed by GUID; presentation controllers callable from `Update()` | none | 2022-04 |
| [Gedemon/HK-Cognomen](https://github.com/Gedemon/HK-Cognomen) | Dynamic empire names from civics/ideology/era | Postfix the game's `SimulationEventRaised_*` handlers (our fire-on-attack pattern) | none | 2022-04 |
| [Gedemon/HK-DelayedAITurn](https://github.com/Gedemon/HK-DelayedAITurn) | "Humans first, then AI" turn mode | Order veto: prefix `DepartmentOfCommunication.ProcessOrder*`, `return false` | none | 2022-03 |
| [shakee2/shakee.Humankind.FameByScoring](https://github.com/shakee2/shakee.Humankind.FameByScoring) | Fame from periodic scoring rounds instead of era stars | `MajorEmpire.Serialize` postfix → mod state inside vanilla saves; full lobby-option recipe | MIT | 2023-01 |
| [rykolu/HkFPCameraModSimple](https://github.com/rykolu/HkFPCameraModSimple) | First-person fly camera (F2) | Disable `PresentationCameraMover`, add own fly-cam, `nearClipPlane = 0.01` → in-game model inspection | none | 2022-01 |
| [rykolu/HkModEntry](https://github.com/rykolu/HkModEntry) | Pre-BepInEx custom mod loader (discontinued) | Anti-pattern (patched game DLL); repo doubles as decompiled `Amplitude.UI` reference | none | 2021-09 |
| [Touhma/Humankind_Plugin](https://github.com/Touhma/Humankind_Plugin) | JSON-configurable balance knobs XML mods can't reach | Postfix `Initialize()` on data definitions; write-defaults-then-read JSON config | none | 2021-09 |
| [lcaresia/realpolitik](https://github.com/lcaresia/realpolitik) | LLM-driven AI nations, currencies and a Central Bank, trade tolls, 16 empires | Hot-reloadable core + file command channel that drives the running game; native UI built by cloning the game's own windows | MIT | 2026-10 |

---

## The Theadd stack (framework + flagship)

One author, one layered stack: **DevTools** is the base library-mod, **GUI-Tools** the flagship built
on it (hard `[BepInDependency]`), **HumankindHacks** a later monorepo re-extracting the framework,
**ModdingTools** an offline scaffolder. All BepInEx 5 / IMGUI.

### Humankind-GUI-Tools ★15 — the in-game toolbox

**Problem solved.** Resurrects the 100+ *hidden debug windows Amplitude shipped disabled inside the
game* (`MilitaryCheats`, `DistrictPainter`, `PawnAnimation`, `Pawns`, `GPUProfilerWindow`, …) and
adapts ~15 into a toolbox: spawn any unit anywhere, paint districts, free camera, cheats, live map
editor.

**How.** All state changes go through the order bus — `SandboxManager.PostOrder(new OrderSpawnArmy {
WorldPosition = …, UnitDefinitions = new[]{ unitDefinitionName } })`, `OrderDamageUnits`,
`OrderChangeUnitsXP`, editor orders like `EditorOrderCreateExtensionDistrictAt`. Reads selection via
`Snapshots.ArmyCursorSnapshot.PresentationData` and resolves GUID → presentation object with
`Presentation.PresentationEntityFactoryController.GetArmy((ulong)guid)`. Extends the sealed
`PresentationCursorController` by caching its private `MethodInfo`s and injecting a custom cursor
subclass — grafting behavior into presentation controllers without Harmony.

**What we could use.**
- **The sim→presentation refresh handshake** — the single most relevant find:
  `PostAndTrackOrder(order, empireIndex).UponCompletion(() => army.DoUpdateMesh(false, false))`.
  A sanctioned "make this pawn re-evaluate its visuals *after* the sim change lands" primitive —
  exactly the sequencing our respawn-after-load and freeze/strip effects currently approximate with
  frame delays.
- **Instant test harness**: `OrderSpawnArmy` + `PresentationCursorController.ChangeToDefaultCursor(new
  StaticString(unitDefName))` spawns our Era-6 units on turn 1. Wiring this to a debug hotkey would
  remove most of our test friction (no more playing/dev-console to reach late eras).
- Multiplayer/version gate: fetches a `VERSION` file from raw.githubusercontent.com and disables all
  tools when `IsOnlineGame && LatestVersion != PLUGIN_VERSION` — cheap desync guard for when the
  Factory ships publicly.

### Modding.Humankind.DevTools — the framework library

**Problem solved.** Gives *other* plugins documented facades (`HumankindGame`, `HumankindEmpire`),
a reliable loaded/new-turn lifecycle, attribute-driven hotkeys (`[InGameKeyboardShortcut]`,
`[OnGameHasLoaded]` — discovered by scanning all loaded assemblies for `[DevToolsModule]`), and an
IMGUI window framework. Hot-reloads via BepInEx ScriptEngine (deploy to `BepInEx\scripts\`).

**What we could use.**
- **Readiness gates, reusable verbatim**: game is safe to touch when `SandboxManager.IsStarted &&
  Amplitude.Mercury.Presentation.Presentation.HasBeenStarted`; UI root exists when
  `GameObject.Find("/WindowsRoot/SystemOverlays") != null`. Its only Harmony patch — postfix
  `AIController.InitializeOnLoad` / `InitializeOnStart`, prefix `OnNewTurnStarted` — is how the whole
  framework learns loaded-vs-new-game-vs-turn. Cleaner anchors than our timer heuristics.
- **The Interop-vs-Simulation dual-access map**: `Amplitude.Mercury.Interop.AI.Entities.*` = fast
  read snapshots, `Amplitude.Mercury.Simulation.*` = live sim. DevTools documents which to use when —
  the seam our pawn code lives in.
- The `[DevToolsModule]` assembly-scan pattern is a ready design if the Factory ever wants
  third-party *model packs* to self-register runtime hooks.
- A central `R.Fields` reflection cache (every private `FieldInfo` in one file) so a game patch
  breaks exactly one file.

### HumankindHacks — plugins without Harmony

**Problem solved.** Cheat/QoL plugins (`EndlessMovingArmies` etc.) plus the framework as shared
projects. Notable: EndlessMovingArmies uses **no Harmony at all** — a coroutine polls until services
exist, then a MonoBehaviour loop reads/writes via `Interop` snapshots.

**What we could use.**
- **`AssetHunter`**: `AssetBundle.GetAllLoadedAssetBundles().Where(b => b.Contains(assetPath))` —
  finds which loaded vanilla bundle contains a named asset *at runtime*. Directly attacks our
  "find the borrowed vanilla visual" problem without the Unity-Inspector detour.
- The zero-Harmony architecture is more game-update-resilient for purely *reactive* effects — worth
  weighing for future per-pawn effects that only need to observe, not intercept.
- Ships its `GUISkin` in an AssetBundle embedded as a **manifest resource** inside the DLL —
  single-file distribution trick for our packaging goal.
- Honesty pattern for MP: announces itself in game chat (`HumankindGame.TrySendChatMessage`) and
  honors an `EnableInOnlineSessions` config.

### ModdingTools — the scaffolder

**Problem solved.** `dotnet`-CLI that generates a complete plugin workspace for any Unity game:
detects the game install, installs BepInEx templates, and — the key part — **publicizes every game
DLL** into `lib/references/` with a `Directory.Build.props` that wildcard-references them and
post-build-copies the plugin to `BepInEx\scripts\` for ScriptEngine hot reload.

**What we could use.**
- **Publicized references would eliminate most of our reflection**: private members become direct
  calls that break at *compile time* after a game update instead of silently at runtime. Strong
  candidate for HumankindAssetFramework.csproj (generate locally, never distribute the publicized DLLs —
  that's how ModdingTools stays in the legal clear).
- The whole repo is a blueprint for our "distributable package for any Humankind modder" goal:
  game-path detection, templates, dry-run UX.

---

## The Gedemon school (simulation-side gameplay mods)

Four repos sharing one toolkit (`HumankindModTool`, credited to "AOM") and one architecture:
publicized DLLs, `PatchAll()` in `Awake()`, patch classes organized by target namespace.
All unlicensed and dormant since 2022 — treat as technique documentation, don't vendor code.

### HK-CultureUnlockFromTerritory — the most complete example

**Problem solved.** "True Culture Location": you can only become Rome if you hold Latium. Territory
renaming, historical city names, culture-change territory loss, extra empire slots.

**What we could use.**
- **The Sandbox lifecycle trio** — the canonical init/teardown skeleton:
  `Sandbox.ThreadStart` prefix = entering game (init per-game state), `ThreadStarted` postfix =
  world fully loaded (safe to touch databases), `ThreadStart` postfix = exiting (clear all statics).
  Our respawn-after-load logic could anchor here instead of timers.
- **`Databases.GetDatabase<T>().Touch(row)`** — the universal "inject a database row at load time"
  API, proven for `LocalizedStringElement` (localization for custom unit names!),
  `GameOptionDefinition`, `UIMapper`.
- **Map-hash-keyed external JSON with LoadOrder merging**: third parties drop `*TCL.json` in a known
  folder; the plugin discovers, validates against a map hash, and merges by priority. A proven shape
  if our `haf_models.json` ever needs multi-pack merging.
- Its `CollectibleManagerPatch` comments enumerate the definition databases relevant to us:
  `PresentationUnitDefinition`, `UnitVisualAffinityDefinition`, `BuildingVisualAffinityDefinition`.
  And its district code shows **visual affinity is mutable simulation state**
  (`district.InitialVisualAffinityName = …`) — the sim-side counterpart of our presentation-side
  pawn work, potentially a cleaner lever someday.

### HK-Uchronia — battle hooks and presentation controllers

**Problem solved.** TCL successor: battle "postures" with forced auto-resolve, AI faction-choice
rework, F3 territory debug overlay.

**What we could use.**
- **Per-battle persistent state**: postfix `Battle.Serialize`, keep a `BattleExtension` keyed by
  `battle.GUID` — the template if fire-on-attack effects ever need battle-scoped state that
  survives saves.
- **Presentation controllers are callable directly from plugin `Update()`**:
  `Presentation.PresentationTerritoryHighlightController.SetTerritoryVisibility(i, bool)`,
  `PresentationCursorController.ChangeToDiplomaticCursor(...)` — same static-hub family as our
  `PawnManager` work; useful for debug overlays on model placement.
- `UIManager.IsUiVisible = false` + a hotkey = clean-screenshot mode for reviewing injected models.
- Parameter smuggling (encoding extra data into an existing int argument between UI order and sim
  processing) — hacky but proven way through the order pipeline without defining new orders.

### HK-Cognomen — the SimulationEvent recipe (and the best-commented tutorial)

**Problem solved.** Dynamic empire titles ("Celtic Kingdom", "People's Democratic Republic") derived
from civics/ideology/era. A single ~1200-line file **deliberately written as a modding tutorial** —
the best "how to write a Humankind Harmony mod" document in the ecosystem.

**What we could use.**
- **The ready-made pattern for our fire-on-attack hook**: instead of subscribing to
  `SimulationEvent<T>.Raised` ourselves, *postfix the game's existing handler methods* — e.g.
  `CultureManager.SimulationEventRaised_CivicChoiceChanged(object sender,
  SimulationEvent_CivicChoiceChanged e)`. The handler already receives the typed event; no
  subscription lifecycle to manage; fires exactly when the sim does. (FameByScoring below shows the
  complementary variant: patching `SimulationEvent_X.Raise` itself.)
- **Presentation-readiness flag**: postfix `PresentationAvatarController.DoStart`/`DoShutdown` to
  flip an `isPresentationStarted` static — a cleaner "presentation is alive" signal than timing.
- Some behavior lives in *stored delegates*, not methods — Cognomen swaps
  `NotificationsController.AllConfigs[i].GetDescription` via `NotificationDataConfig.Forge(...)`.
  Worth remembering when a Harmony target seems to not exist.
- Its comments also document: prefer postfix over replacement "to reduce mod's maintenance between
  game's patches"; the game writes HTML logs to `Documents\Humankind\Temporary Files`.

### HK-DelayedAITurn — the minimal-viable plugin

**Problem solved.** "Humans first" turn mode: AI empires wait until every human has clicked End Turn.
One file, ~230 lines, five patches — a good skeleton reference.

**What we could use.**
- **Order interception**: prefix `DepartmentOfCommunication.ProcessOrderEmpireReady(order)` and
  `return false` to swallow the order — there's a `ProcessOrderX` per order type, so this is the
  general veto point in the turn pipeline.
- `AIController.RunAIDecisionCycle` marks the AI-turn boundary; the sandbox runs on its own thread
  (the mod blocks it with `Thread.Sleep` polling — possible there, never on the main thread).

---

## The singles

### shakee2/shakee.Humankind.FameByScoring — save-file extensions and lobby options (MIT!)

**Problem solved.** Fame from periodic Military/City/State/Economy scoring rounds instead of era
stars, with ~10 new lobby options and a fame-history tooltip on the empire banner.

**What we could use.**
- **Mod state inside vanilla saves** — the standout pattern, and one of only two MIT repos:
  a `MajorEmpireExtension : Amplitude.Serialization.ISerializable` holding mod data, written/read by
  a postfix on `MajorEmpire.Serialize(Serializer)` via
  `serializer.SerializeElement("MajorEmpireExtension", …)`, switching on
  `serializer.SerializationMode` (Read/Write). Allocation on `MajorEmpire.InitializeOnStart` postfix,
  teardown on `Sandbox.ThreadStart`. Adaptable to per-unit state — if model assignments or
  freeze-flags ever need to survive save/load without our sidecar JSON, this is how.
- **Session-type detection**: its `Sandbox.ThreadStart` patch casts
  `SandboxThreadStartSettings.Parameter()` to `SandboxStartSettings` / `ScenarioStartSettings` /
  `GameSaveDescriptor` to distinguish new game vs save-load vs scenario — a more principled home for
  our `respawnAfterLoad` flag than detecting after the fact.
- **The full lobby-option recipe** (`HumankindModTool/GameOptionHelper.cs`): prefix
  `OptionsManager<GameOptionDefinition>.Load`, `ScriptableObject.CreateInstance<GameOptionDefinition>()`,
  `Touch()` it plus `%{key}Title` localization plus `OptionUIMapper` rows → a real in-game option
  instead of a BepInEx config file. Read back via
  `Services.GetService<IGameOptionsService>().GetOption(new StaticString(key)).CurrentValue`.
- Proven combat-event targets for us: `SimulationEvent_BattleTerminated.Raise`,
  `SimulationEvent_UnitKilledByOther.Raise`, `SimulationEvent_NewTurnBegin.Raise`.
- **UI injection by transform path**: `GameObject.Find("WindowsRoot/InGameOverlays/EmpireBanner/
  _FamePennant/FameScore/")` then attach a child GameObject with Amplitude UI components — the
  quick-and-dirty way to add HUD elements.

### rykolu/HkFPCameraModSimple — the in-game inspection camera

**Problem solved.** F2 toggles a first-person WASD fly camera. ~150 lines, no Harmony.

**What we could use.**
- **A near copy-pasteable in-game model-inspection camera**: `GameObject.Find("Camera")` →
  `GetComponent<PresentationCameraMover>()` (Amplitude's RTS camera driver) → `enabled = false`,
  add your own fly-cam MonoBehaviour, set `nearClipPlane = 0.01f`. Lets us orbit injected pawns at
  close range in the real renderer — the truthful complement to our editor preview (a custom posed
  viewer is impossible; this sidesteps it entirely).
- Its comments document the scene's cameras (`ImpostorCamera`, `AvatarCamera`, `Camera`, `MainView`,
  `UIFxCamera`) and the pawn render layers: `PresentationPawn`, `PresentationPawnHidden`,
  `PresentationPawnGhost`, `PresentationPawnGhostAndOpaque` — useful if we ever need layer tricks on
  injected geometry.

### rykolu/HkModEntry — the cautionary tale

**Problem solved.** A 2021 mod loader that patched `Amplitude.UI.dll` itself (decompile → inject a
DLL-scanner into `UIUpdatingManager.Update()` → recompile). Author's own verdict: "Discontinued,
since BepInEx does this but better."

**Takeaways.** Validates our BepInEx no-exe-patching approach — replacing shipped DLLs breaks every
game update. Two residual nuggets: a convention-based plugin contract (`<Name>.Main.Load()` +
optional per-frame `OnUpdate`) if the Factory ever wants drop-in model packs with runtime hooks; and
the repo is a grep-able decompiled snapshot of `Amplitude.UI` circa 1.0.x.

### Touhma/Humankind_Plugin — reaching what XML mods can't

**Problem solved.** Makes code-initialized balance values (game-speed multipliers, era-star
requirements, pollution thresholds, end-game conditions) configurable via JSON — the plugin half of
a data-mod + plugin pairing, existing precisely because XML/data mods can't touch definition fields
set in code.

**What we could use.**
- **Postfix `Initialize()` on data definitions** (`GameSpeedDefinition`, `EraDefinition`, …) — the
  general lever for anything living in an `Amplitude.Mercury.Data.Simulation` definition. Relevant
  if the registry ever needs to adjust `UnitDefinition`/pawn definitions in code rather than assets.
- **Write-defaults-then-read config**: first run serializes the built-in defaults *out* to
  `BepInEx/config/...json`; later runs read user edits back. Self-documenting — a nice touch for
  `haf_models.json` template generation.
- Compact Harmony idioms: `___PrivateField` parameter injection to read private manager state;
  prefix with `__result` + `return false` for full replacement.
- A (disabled) postfix on `Databases.GetDatabase(Type)` that dumps **every** game database to JSON —
  a poor-man's data-mining tool worth resurrecting for exploration.
- Manages publicized game DLLs in per-game-version folders (`1.0.2.139/`, `1.3.248/`) — sane layout
  for tracking game patches.

---

## The 2026 arrival: lcaresia/realpolitik

*Added 2026-10-10, read at commit `cc059a5` (v1.2.0). Verified against the code unless a line says otherwise. Most of
its docs and all of its code comments are in Portuguese.*

One author, one large mod (~44k lines of C#), MIT, and the only repo in this survey that targets a current game
build: HUMANKIND 1.31.4836 on BepInEx 5.4.23.5. Three assemblies: a fixed **Loader** plugin, the hot-reloadable
**core** (`CurrencyMod`), and a separate **MoreEmpires** plugin.

**Problem solved.** Every computer nation "thinks" once per turn with a language model: it writes letters, keeps a
diary and memory, and its decisions become real game orders. Around that: a currency per empire with inflation and a
Central Bank, trade tolls and blockades, and 2 to 16 empires on any map size.

**How (the LLM part).** The model never touches the simulation.

1. A postfix on `SandboxState_TurnMain.Begin` copies the world into plain objects on the game thread, after
   start-of-turn processing and before the native AI acts (`Diplomacia/Capture/TurnCapture.cs`).
2. A text "dossier" per nation is built from that copy, filtered by the game's own fog-of-war rules, with stable
   codes (`E3`, `C14`, `A7`, `T31`) for everything the model may reference.
3. The request runs on the `ThreadPool` over plain `HttpWebRequest`; the end of turn never waits for it.
4. The answer is JSON with a **closed list of actions**. A parser validates it against what that nation may cite and,
   on errors, asks again with the error list (`Diplomacia/IaJobs.cs`).
5. Actions are queued and drained in a prefix on `PostOrderController.Update` (the sandbox thread), pre-checked with
   the game's own refusal functions (`GetDiplomaticActionFailureFlags`), then posted with
   `SandboxManager.PostAndTrackOrder` (`Diplomacia/ActionExecutor.cs`).
6. The ticket result, or the game's refusal reason, goes back into the nation's memory for its next dossier.

The native AI keeps running the empire. About 20 patches on `Amplitude.Mercury.AI.Brain` analysis and generator
classes take only the "leader" decisions away from it (`NativeAiLocks.cs`), and a "focus" setting multiplies its own
goal and need scores instead of issuing build or research orders (`NativeAiBias.cs`). Tactical battles stay with the
native battle AI.

**What we could use.**
- **Hot reload without ScriptEngine** (`src/CurrencyMod.Loader/LoaderPlugin.cs`, ~230 lines). The core DLL lives
  *outside* `BepInEx\plugins`, so BepInEx never loads it; the fixed loader polls its write time every 0.5 s, waits
  for it to stop changing, calls `ModEntry.Stop()` (which unpatches Harmony and returns state as JSON) and
  `ModEntry.Start()` on the new bytes. Two traps it documents and solves:
  - Unity binds `MonoBehaviour` types by **assembly name**, so each generation is renamed with Mono.Cecil
    (`CurrencyMod_g{N}_{ticks}`) before `Assembly.Load(bytes)`. Without it, `AddComponent<T>()` keeps using the first
    generation's classes.
  - Each generation has fresh statics. State a patch reads constantly must cross over *before* the new patches go
    in, via `AppDomain.CurrentDomain.SetData` in `Stop` and a read at the top of `Start`.
- **A file command channel that drives the running game** (same loader, plus `DevTools.cs` and `GameCommands.cs`).
  One command per line in `_Modding\dev\cmd.txt`; the file is deleted on read and results append to
  `dev\out\result.txt`. Commands include `screenshot`, `rec` (frames at a locked `Time.captureFramerate`), `tree` /
  `inspect` / `find` over the UI hierarchy, `click` and `hover` on a UI path, and `jogo carregar` / `jogo turno` /
  `jogo novo` to load a save, end the turn or start a game. The channel only exists when the dev folder does. This
  is the shape of an in-game drill that needs nobody at the keyboard.
- **Native UI by cloning donor windows** (`NativeUI/NativeUIKit.cs`, and the guide `docs/guia-telas-nativas.md`).
  The game's look lives in configured prefab components, so the recipe is: instantiate a native window under an
  inactive parent, strip its game-specific scripts, add your own `UIWindow` subclass (never `GameWindow`), and
  register it with `WindowsManager.Instance.GetWindowsGroup<T>().AddDebugWindowImplementation(window)`. The guide
  also has a catalogue of tested donors, a way to add a tab to a screen whose tabs are a fixed enum, the SDF icon
  format, and a list of traps. Relevant if HAF's F8 panel ever moves off IMGUI.
- **Mod state inside the save, the simple way** (`Diplomacia/IaSavePatches.cs`). Postfix
  `Sandbox.Save(string, IStorageContainerWriteHandle, SerializationFormat, GameSaveDescriptor)` and write a file with
  `writeHandle.OpenDataWrite("Name.json")`; postfix `Sandbox.Load(IStorageContainerReadHandle, GameSaveMetadata)`
  and read it with `readHandle.OpenDataRead`, treating `FileNotFoundException` as "save from before the mod". The
  game ignores files it does not know, so the save still opens without the mod. Less invasive than FameByScoring's
  `Serialize` postfix above.
- **Publicized references as a package**: `Krafs.Publicizer` in the csproj, with `<DoNotPublicize>` entries for the
  methods the mod overrides (they must keep their original modifier). This is the ModdingTools idea above with no
  tooling of our own.
- **Building without the game** (`refs/`, `tools/ci/preparar-referencias.ps1`). 21 reference assemblies with every
  method body stripped by JetBrains Refasmer, so CI can compile on a machine with no game installed. The script lays
  them out as a fake game folder and refuses to run if a real `Humankind.exe` is there. The author states these
  files are outside the MIT licence; committing them is a licensing judgement we have not made.
- **The order bus, confirmed on a current build.** `PostAndTrackOrder(order, empireIndex)` returns a
  `PostOrderTicket`; `ticket.UponCompletionWithParam(...)` fires when the sim has processed it and
  `ticket.Result == PostOrderResponse.Valid` says whether it was accepted. Same primitive as GUI-Tools above, and
  here it is posted from the sandbox thread only while `sandbox.CurrentStateName == "SandboxState_TurnMain"`.
- **Thread discipline for patches on the AI thread.** The native AI runs on its own thread. Every table its patches
  read is a `volatile` reference to an array that is replaced whole, never edited in place, and every patch body is
  wrapped so an exception cannot stop the game's order loop.
- **A map of the game's internals** (`research/`). Thirteen notes, including a catalogue of every order type with
  its validation site (`order-catalog.md`), the native AI's army behaviour (`army-orders.md`) and the UI framework
  (`native-ui.md`). They cite line numbers in the author's own decompile, so use them as an index into ours.
- **The LLM pattern itself, and its bench.** Snapshot → dossier → closed-list JSON → validate against the real
  vocabulary → retry with the errors → apply through the existing code path → feed the outcome back. `tools/ia-bench`
  replays logged decisions outside the game to compare prompts and models before a change ships. Candidate shape for
  an editor assistant that proposes part marks from a probed part list; nothing is built.
- **Release pipeline** (`tools/gerar-release.ps1`, `.github/workflows/release.yml`). A deterministic build with no
  symbols, a file whitelist, a gate that scans every staged file (DLLs and the zip included) for API-key patterns
  and local machine paths, `SHA256SUMS.txt`, and a GitHub build-provenance attestation on tagged releases.

---

## Cross-cutting lessons, ranked by value to us

1. **The order bus + completion callback** (`SandboxManager.PostAndTrackOrder(...).UponCompletion(...)`
   → `PresentationArmy.DoUpdateMesh`) is the ecosystem's sanctioned sim→presentation sequencing
   primitive. Our respawn-after-load and any future per-pawn effect should evaluate it.
2. **Lifecycle anchors instead of timers** — a complete, proven set exists:
   `Sandbox.ThreadStart` (prefix=enter / postfix=exit) + `ThreadStarted` (world ready) +
   `Presentation.HasBeenStarted` + `PresentationAvatarController.DoStart/DoShutdown` +
   session-type detection via `SandboxThreadStartSettings.Parameter()` casts.
3. **Two proven combat-hook idioms** for fire-on-attack-style effects: postfix the game's
   `SimulationEventRaised_*` handler methods (Cognomen), or patch `SimulationEvent_X.Raise` itself
   (FameByScoring — `BattleTerminated`, `UnitKilledByOther` are proven targets).
4. **`Databases.GetDatabase<T>().Touch(row)`** injects database rows at load time — localization for
   custom unit names, real lobby options (full recipe in FameByScoring's `GameOptionHelper`),
   UI mappers.
5. **Save-file extensions** — postfix `Serialize(Serializer)` on any sim entity + an `ISerializable`
   sidecar = mod state inside vanilla saves, no external files.
6. **Publicized assemblies** (ModdingTools' generate-locally approach) would convert our runtime
   reflection risk into compile-time errors after game patches.
7. **Dev-velocity trio**: `OrderSpawnArmy` debug spawner + FP inspection camera + ScriptEngine
   hot reload would together remove most of our test friction.
8. **Licensing reality**: only GUI-Tools, DevTools, FameByScoring and (since 2026-10) Realpolitik are MIT. Everything else has
   *no license* (default all-rights-reserved) — borrow techniques and patch targets freely, never
   copy code verbatim into anything we distribute. Several repos also commit game DLLs; don't
   imitate that.
9. **Everything is dormant** (mostly 2021–2022; only GUI-Tools saw 2024 activity). Patch targets
   documented here predate recent game versions — treat this file as a map, and re-verify each
   signature with ilspycmd before use. *2026-10-10:* Realpolitik is the exception; its targets are against
   game build 1.31.4836.
10. **Drive the running game from a file** *(added 2026-10-10)*: Realpolitik's hot-reloadable core plus its
    `cmd.txt` command channel (load a save, end the turn, screenshot, click a UI path) is the most complete
    dev loop in the ecosystem, and the closest thing to an unattended in-game drill.
