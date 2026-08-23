# Headless CLI & the mod build/deploy pipeline

Makes HAF **operable** without the GUI: the editor's authoring functions run from the command line via Unity batch
mode, so an agent, a script, or CI can do what a human does in `Tools ▸ HAF` — including the **full mod build + deploy**.
Rationale/scoping in [Headless-CLI-Design.md](notes/Headless-CLI-Design.md).

**Status (2026-08-02):**
- ✅ **`rebuild-model`** — re-bake a model headless (Unity batch). **Verified** (`AttackHelicopter`: assets written, registry updated, exit 0).
- ✅ **`clean-export`** — remove the deployed mod (Unity batch). **Verified**.
- ✅ **`build-mod`** — **full build + deploy**, headless, end-to-end. Runs the game's own Mod Editor build steps past the batch-hostile database gate, stamps the mod + game version, and deploys the versioned module to Community. **Verified in-game** (2026-08-02: built `1.3201`, `GameVersion 1.30.4814`, loads with no compatibility warning).
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

The Community folder is **resolved, not hardcoded** (`HafPaths`, 2026-08-23): a saved override, else
`<Documents>/Humankind/Community`, else unknown. When it is unknown this verb **fails with exit 2** and names both
fixes. That failure is the point: until 2026-08-23 the path was a `const` naming one developer's junctioned layout,
so on any other machine the verb found nothing and reported `{"ok":true,"removed":0}` — **byte-identical to a
successful clean.** Batch mode has nobody to prompt, so it must fail loudly instead of succeeding emptily.

### `build-mod` ✅ (full build + deploy)
`-executeMethod HAF.Cli.BuildMod [-strict]`. Reproduces, via reflection, exactly what clicking **Build** in the Mod
Editor does — but headless. **Pre-ship validation first (2026-08-18):** before any build step, the shared
pack-validator rule core (see [Pack-Validator-Design](notes/Pack-Validator-Design.md)) runs over the FULL registry —
the last gate before the pack leaves the machine. Default: issues are logged and the build continues (fail-soft,
matching in-game behaviour); with **`-strict`** any issue FAILS the build with **exit 4** — the CI-able stop-ship
mode. It does **not** call the top-level `BuildModification` wrapper (that method's first act is a database gate
that hard-aborts in batch mode — see [Database validation](#database-validation)). Instead it calls the three private
build steps that sit *past* the gate, in order:

`TryBuildModification(RuntimeModule, StandaloneWindows64, out msg)` → `DistributeModification(…, false)` →
`CopyModification(…, false)` — the last copies the versioned module into the game's Community folder.

**Version stamping (critical).** `TryBuildModification` stamps `runtimeModule.Version` and `runtimeModule.GameVersion`
from two statics that the editor's *version panel* normally pre-loads — and a batch run never draws that panel, so both
default to `0.0`. A `GameVersion 0.0` makes the game reject the mod as *"built using another game version."* So `BuildMod`
runs the same prep first: `LoadTargetMercuryApplicationVersionIFN()` (reads the **game exe's** `FileVersionInfo` →
`GameVersion`) and `TryResetNextModificationVersion()` (current mod `Version` + 1 → next). The mod version is read from the
module asset (`Assets/Runtime/ENCReload.asset`) each run, so it self-increments across builds just like the GUI. The exit
line reports the stamped `version` + `gameVersion`. Reflection keeps the editor compile-check independent of the Mercury SDK DLL.

## The mod build→deploy mechanism (discovered)

The user's "build the mod" is the menu **`Mercury ▸ Mod Editor`** (`ModuleEditor`). The top-level
`ModuleEditor.BuildModification` (in `ModuleEditor.Distribution.cs`) wraps a pipeline whose meaningful steps are:

1. **Database check** — `DatabaseChecker.CheckDatabases()`, first thing in the wrapper. In batch mode a DB error
   **aborts the build** with no dialog; in the editor it shows a *"database has errors, build anyway?"* prompt you click
   past. *`BuildMod` skips this gate — see [Database validation](#database-validation).*
2. **Apply version** — `TryApplyNextModificationVersion()` stamps `Version` (next mod version) and `GameVersion` (game exe
   version) onto the module. **Both come from statics the version panel pre-loads**, so a headless build must load them
   itself (see `build-mod` above).
3. **Build the versioned module** — the asset bundle is built with the **GUID+version baked into its AssetBundle name**,
   so the versioned bundle's **CRC differs** from a raw build (raw `3323885555` vs versioned `3379712144`) — you cannot
   fake it by renaming. GUID from `PlayerSettings.productGUID`. Output: `Assets/AssetBundles/StandaloneWindows64/ENCReload.<GUID>.<version>/`.
4. **Deploy** — `CopyModification(...)` copies the module into the game's Community folder, whose path is **computed, not
   hardcoded**: `GetCommunityFolderPath()` = `Path.GetFullPath(Application.GameDirectory + "/../Humankind/Community")`.

`BuildMod` calls steps 3–4 directly (plus the step-2 prep) and skips step 1, so it inherits correct versioning and the
config-derived deploy target with no re-implementation. (Earlier dead end: `AssetBundleBuildSettings.Build` alone only
produces the *raw* un-versioned bundle; the versioning + deploy live in `ModuleEditor`, one level up.)

### Database validation

The wrapper runs `DatabaseChecker` before building. On ENC it flags
`NullReferenceException: Null class reference for AirUnit_Era5_Common_Biplanes` — but this is a **spurious pre-build
validation error, not a data bug**: the check can't resolve `UnitClass_FighterAircraft` yet (Biplanes' class block is
byte-identical to the working MonoplaneFighters), and the **real build resolves it fine**. In the editor you click
*"Build anyway"* and it works — which is why the mod has always built despite the console error. `BuildMod` does the
headless equivalent: it **skips the DB gate** and runs the build steps past it (the editor's "Build anyway" path), rather
than letting a false positive hard-abort the batch run. If you ever want the gate enforced, run the check separately —
don't route the build through the wrapper.

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
- `build` → **full end-to-end success.** Skipped the spurious Biplanes DB gate, stamped `Version 1.3201` +
  `GameVersion 1.30.4814`, built the ~230 MB versioned bundle, and deployed the 4 core files to Community.
  **Verified in-game:** loads with no *"built using another game version"* warning, rebaked TankDestroyer present.
  (First bypass attempt shipped `0.0`/`GameVersion 0.0` and was rejected — root cause was the un-loaded version statics,
  now fixed by the version prep.)
