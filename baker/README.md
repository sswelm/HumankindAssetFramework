# `baker/` — the live pipeline pieces that live in THIS repo

Everything in this folder has exactly one home, and it is here. There is no snapshot, no mirror, and nothing
"kept for reference" — see the history at the bottom for why that rule exists.

## ✅ `glbconv/` — the GLB→OBJ converter (single source of truth)

`Program.cs` + `glbconv.csproj` + the pinned `SharpGLTF.Core.dll`. **This is the only copy of the source
anywhere.** ENCReload holds just the deployed `glbconv.exe` and its `BUILD.md`.

Build with `dotnet publish -c Release`, then copy the published exe over `<ENCReload>/Tools/glbconv/glbconv.exe`
— and **A/B-diff old-vs-new OBJ output before deploying** (procedure in ENCReload's `Tools/glbconv/BUILD.md`).
That diff is not optional: the 2026-08-16 rebuild shipped with the T5 mirrored-winding fix silently regressed,
and the A/B is what would have caught it.

## ✅ `reactor_silhouette.py` — the district-silhouette helper

Headless Blender; documented in [`docs/District-Dedicated-Visual.md`](../docs/District-Dedicated-Visual.md).
Also lives only here.

## Where the editor tooling actually is

The **HAF Authoring Tools** (Model Factory, Animation Lab, District Factory, Prop Lab, Projectile Lab, Vehicle
Lab, Sound Studio, Backup & Restore, Ship Status) are Unity editor scripts and live **only** in the
[ENCReload](https://github.com/sswelm/ENCReload) project, under `Assets/Scripts/Editor/`. Edit and bake there.

They cannot run from this repo in any case: `HumankindAssetFramework` is a plain .NET project, not a Unity
project, so `UnityEditor`-dependent scripts do not compile here.

## Why there is no snapshot of them here any more (2026-08-21)

There used to be one — 13 files, ~7,000 lines, mirrored from ENCReload and labelled a "deliberately stale
reference snapshot". It was deleted, and the reasoning is worth keeping because it reverses an earlier call:

- **It was documented as dangerous rather than made safe.** Its own README warned that this copy's `ModelDef`
  was missing fields the plugin reads (`scale`, `animPhaseSpread`) plus bake-time ones (`staticParts`,
  `localNodeAnim`, `bakeLocked`, `deployStripExtra`), so writing a `pack.json` from it would **silently omit**
  them and the affected models would render at default scale and phase with no error anywhere.
- **The same disease had already cost a shipped regression** — glbconv's two diverged sources, where each copy
  held a fix the other lacked (CHANGELOG 2026-08-17). A copy without a sync guard eventually ships a bug.
- **The original reason to keep it no longer applied.** In 2026-08-01 a *blanket* delete of `baker/` was
  genuinely unsafe: the folder also held the live `glbconv/` and a `Tools/` Blender-script copy, so the
  snapshot was documented loudly instead. The Blender copies were deleted on 2026-08-17, and a **targeted**
  delete of just the editor `.cs` — leaving `glbconv/` and the `.py` untouched — carries none of that risk.
- **It gets more dangerous the moment strangers arrive.** Today it is a hazard a maintainer knows about. After
  a public release it is plausible-looking editor source that an adopter finds, drops into Unity, bakes from,
  and produces quietly broken packs with.

Nothing was lost: the files remain in git history, and the authoritative versions are in ENCReload.

The `HumankindAssetFramework.csproj` exclusions (`DefaultItemExcludes` + `Compile Remove="baker\**\*.cs"`) are
still required — they keep glbconv's `Program.cs` and its self-contained .NET 8 publish output out of the
plugin build.
