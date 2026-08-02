# Headless CLI & the mod build/deploy pipeline

Makes HAF **operable** without the GUI: the editor's authoring functions run from the command line via Unity batch
mode, so an agent, a script, or CI can do what a human does in `Tools ▸ HAF` — including the **full mod build + deploy**.
Rationale/scoping in [Headless-CLI-Design.md](Headless-CLI-Design.md).

**Status (2026-08-02):**
- ✅ **`rebuild-model`** — re-bake a model headless (Unity batch). **Verified** (`AttackHelicopter`: assets written, registry updated, exit 0).
- ✅ **`clean-export`** — remove the deployed mod (Unity batch). **Verified**.
- ✅ **`build-mod`** — **full build + deploy**, wired to the game's own Mod Editor (`ModuleEditor.BuildModification`). Runs the real editor pipeline headless (build the versioned module → deploy to Community). Reaches the pipeline correctly; a clean end-to-end run is currently **blocked by a pre-existing content error** (see [Database validation](#database-validation) — the same error blocks the *editor* build).
- ✅ **`Tools/haf-deploy.bat`** — deploy-only, a **pure file copy (no Unity)**. **Verified**. Fallback for when you build in the editor and just want to (re)deploy.

## Setup

- **Unity** must be present (same requirement as the editor): `C:\Program Files\Unity 2021.3.1f1\Editor\Unity.exe`
  (Unity Hub ▸ Installs, or the Start-menu shortcut target).
- **Close the Unity editor first** — a project can't be open twice; batch mode fails on a locked project.
- **First run is slow** (~1 min+ import + domain reload; a mod build adds the ~230 MB asset-bundle pack).

Wrapper: `Tools/haf.bat` (in the [ENCReload](https://github.com/sswelm/ENCReload) repo, where the CLI code lives):

```
haf rebuild <resourceName> [-fresh]   re-bake one model (-fresh forces a full re-slim)
haf rebuild-all                       re-bake every model with a source file
haf build                             FULL mod build + deploy (the Mercury Mod Editor build, headless)
haf clean                             remove the deployed ENCReload Community export
```
Deploy-only (no Unity): `Tools\haf-deploy.bat`. Each Unity verb prints one `[HAF-CLI] {…}` JSON line; exit `0` ok, `2` bad-arg/not-found, `3` failed.

## Verbs

### `rebuild-model` ✅
`-executeMethod HAF.Cli.RebuildModel -model <name> [-fresh]` (or `-all`). Reuses the **exact** GUI path
(`ModelRegistry.Load` → `ModelFactoryWindow.ConfigFor` → `UniversalBaker.Build/BuildAnimated` → copy `BakeResult` GUIDs →
`ModelRegistry.Upsert`) — can't drift from the Bake button. Writes `pack.json` + `Assets/Resources/<name>_{ModelMesh,Skeleton,Atlas}.asset`.

### `clean-export` ✅
`-executeMethod HAF.Cli.CleanExport`. Deletes `Community\ENCReload.<GUID>.*` (the *"move your mod … is denied"* fix,
scoped to GUID `cd3480e932114f8084db755ddd65f2d8`). It removes the **live** deployed mod — a pre-build cleanup.

### `build-mod` ✅ (full build + deploy)
`-executeMethod HAF.Cli.BuildMod`. Calls the game's own **Mod Editor** build via reflection:
`Amplitude.Mercury.Production.Modification.ModuleEditor.BuildModification(RuntimeModule, StandaloneWindows64, false)` —
the **private, synchronous, batch-mode-aware** overload (the public `BuildModification(bool)` schedules via
`EditorApplication.delayCall`, which never runs under `-quit`). This one method does the whole thing: **builds the
versioned runtime module and deploys it to the Community folder**. Reflection keeps the editor compile-check independent
of the Mercury SDK DLL.

## The mod build→deploy mechanism (discovered)

The user's "build the mod" is the menu **`Mercury ▸ Mod Editor`** (`ModuleEditor`). Its build
(`ModuleEditor.BuildModification`, in `ModuleEditor.Distribution.cs`) is a self-contained pipeline:

1. **Database check** — `DatabaseChecker.CheckDatabases()`. In batch mode a database error **aborts the build** (returns
   false, no dialog). *This is the SDK's own gate — see below.*
2. **Build the versioned module** — the asset bundle is built with the **GUID+version baked into its AssetBundle name**,
   so the versioned bundle's **CRC differs** from a raw build (raw `3323885555` vs versioned `3379712144`) — you cannot
   fake it by renaming. The version comes from `Amplitude.Framework.Editor.Automation.ProjectVersion` (git-revision-derived);
   the GUID from `PlayerSettings.productGUID`. Output: `Assets/AssetBundles/StandaloneWindows64/ENCReload.<GUID>.<version>/`.
3. **Deploy** — `CopyModification(...)` copies the module into the game's Community folder, whose path is **computed, not
   hardcoded**: `GetCommunityFolderPath()` = `Path.GetFullPath(Application.GameDirectory + "/../Humankind/Community")`.

Because the CLI invokes this exact method, it inherits all three steps — including correct versioning and the
config-derived deploy target — with no re-implementation. (Earlier dead end: `AssetBundleBuildSettings.Build` alone only
produces the *raw* un-versioned bundle; the versioning + deploy live in `ModuleEditor`, one level up.)

### Database validation

`BuildModification` runs the SDK's `DatabaseChecker` first, so a **content error fails the build** — for both the CLI
*and* the editor. First live run surfaced: `NullReferenceException: Null class reference for AirUnit_Era5_Common_Biplanes`
(`ModuleEditor.Distribution.cs:191`). That is a **mod-data bug** (a broken class reference on the Biplanes Era5 unit),
not a CLI fault — and it's almost certainly why the editor build "stopped working like it used to." The last good build
(`1.3199`) predates it. **Fix the data → the build (CLI or editor) completes and deploys.** This is a feature, not a
regression: the headless build enforces the same validation the editor does.

## Deploy-only — `Tools/haf-deploy.bat`

A **pure file copy (no Unity)** for when the versioned module already exists (e.g. built in the editor) and you just want
it in Community. Finds the newest `Assets/AssetBundles/StandaloneWindows64/ENCReload.<GUID>.<version>/`, cleans old
Community exports, and copies the **4 core files** — stripping the `.meta` files **and** the `.assetbundle.txt` (matches
the editor's own deploy exactly; verified by diffing). Useful as a fallback and for understanding the deploy contract.

## Implementation

`ENCReload/Assets/Scripts/Editor/HafCli.cs` — `namespace HAF { static class Cli }`, verbs `RebuildModel` / `CleanExport` /
`BuildMod`. Batch-mode arg parse via `Environment.GetCommandLineArgs`; JSON result via `Debug.Log("[HAF-CLI] …")`; exit via
`EditorApplication.Exit(code)`. SDK types (`ModuleEditor`) resolved by name via reflection so the editor compile-check
stays independent of the Mercury DLL. Compile-checked (`bash Tools/editor_compile_check.sh`).

## Verification record (2026-08-02)

- `clean` → `{"ok":true,"removed":1}`, exit 0.
- `rebuild AttackHelicopter` → `[Factory] AttackHelicopter DONE …`, assets rewritten, `pack.json` updated, `{"ok":true,"rebuilt":1}`, exit 0.
- `haf-deploy.bat` → deployed the 4 core files; in-game: `[GameBinding] OK — 31 type(s)`, `22 model(s), 0 conflict(s)`.
- `build` → correctly invoked `ModuleEditor.BuildModification` and ran the real pipeline; **failed on the Biplanes
  database error** (a content bug, not the CLI). End-to-end success pends the data fix.
