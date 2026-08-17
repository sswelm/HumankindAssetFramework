# `baker/` — what is and isn't authoritative here

This folder inside the **plugin** repo holds two very different kinds of thing. Read this before touching anything in it.

## ⚠ The editor `.cs` files are a STALE REFERENCE COPY — do not edit or bake from them

`ModelRegistry.cs`, `ModelFactoryWindow.cs`, `AnimationLabWindow.cs`, `UniversalBaker.cs`, `VehicleLabWindow.cs`,
`SoundWindow.cs`, `RetextureWindow.cs`, `ClipRangeDialog.cs`, `BackupWindow.cs`, `SocketBonesDialog.cs`,
`PawnRigDumpWindow.cs`, `BakeSmokeTest.cs`, `ConversionGateTest.cs` are a **snapshot** of the Unity editor tooling.

- **Authoritative source:** the Unity project at `C:\Repo\ENCReload\Assets\Scripts\Editor\`. Always edit and **bake there.**
- **This copy is NOT kept in sync.** It drifts behind ENCReload — e.g. this `ModelDef` (`ModelRegistry.cs`) is missing
  runtime fields the plugin actually reads (`scale`, `animPhaseSpread`) plus several bake-time ones (`staticParts`,
  `localNodeAnim`, `bakeLocked`, `deployStripExtra`). Writing a `pack.json` from this stale `ModelDef` would silently
  **omit** those fields, so the affected models would render at default scale / default phase-spread with no error.
- **It is inert in this repo anyway.** `HumankindAssetFramework` (this repo) is a plain .NET project, not a Unity project, so these
  `UnityEditor`-dependent scripts don't compile or run here. The plugin build excludes them
  (`HumankindAssetFramework.csproj` → `<Compile Remove="baker\**\*.cs" />`). They exist only as a rough reference.

If you want the current editor behaviour, read/run it in **ENCReload**, not here. (The plugin runtime — `Plugin.cs`,
`Patches/`, at the repo root — is the code that actually ships in this repo and is kept current.)

## ✅ `baker/glbconv/` — LIVE: the single source of truth for glbconv (2026-08-17)

The GLB→OBJ converter: `Program.cs` + `glbconv.csproj` + the pinned `SharpGLTF.Core.dll`. **This is the only copy
of the source anywhere.** ENCReload's `Program.cs.src` snapshot was deleted after the two copies split-brained —
each had accumulated a verified fix the other lacked, and the 2026-08-16 exe rebuild shipped with the T5
mirrored-winding fix regressed (CHANGELOG 2026-08-17). Build with `dotnet publish -c Release`, then copy the
published exe over `<ENCReload>/Tools/glbconv/glbconv.exe` — and **A/B-diff old-vs-new OBJ output before
deploying** (procedure in ENCReload's `Tools/glbconv/BUILD.md`).

`baker/reactor_silhouette.py` is also live-from-here: the district-silhouette helper documented in
`docs/District-Dedicated-Visual.md`.

## Blender scripts — deleted from here (2026-08-17), live ONLY in `<ENCReload>/Tools/`

The pipeline's Blender scripts (`rig_anim.py`, `prep_model.py`, `vehicle_rig.py`, `deploy_convert.py`, …) run from
`<UnityProjectRoot>/Tools/` — the copies that used to sit at `baker/` root and in `baker/Tools/` were **never
executed by anything** and had drifted weeks behind while this README labelled them "live". That's the same
two-copies disease that caused the glbconv regression, so they were deleted outright rather than documented.

## Why not just delete the stale `.cs` files?

They were left documented rather than deleted (2026-08-01, general-review finding) because the folder is entangled with
the live `glbconv`/`Tools` above and the csproj notes `baker/` "ships in the mod". If the stale editor snapshot is ever
formally distributed as tooling, it must be **re-synced from ENCReload first** — never shipped as-is.
