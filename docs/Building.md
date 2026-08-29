# Building the plugin

For adopters who just want to *use* the Factory, no build is needed — grab a released `HumankindAssetFramework.dll`. Build only if
you're changing the plugin.

## Plugin (`HumankindAssetFramework.dll`)

Needs the **.NET SDK**. The project file is `HumankindAssetFramework.csproj`. Put a `References\` folder next to the `.csproj` containing:

- `BepInEx.dll` + `0Harmony.dll` + `MonoMod.RuntimeDetour.dll` + `MonoMod.Utils.dll` + `Mono.Cecil.dll` — from
  `<Humankind>\BepInEx\core\` (the MonoMod/Cecil trio are Harmony's own runtime deps — the unit suite EXECUTES
  patches for the shared-seam census tests)
- `UnityEngine*.dll` + `Newtonsoft.Json.dll` — from `<Humankind>\Humankind_Data\Managed\`
  (Newtonsoft is provided by the game at runtime — used for robust registry parsing, since `UnityEngine.JsonUtility`
  silently returns empty in the game's Mono runtime; `Private=false`, so it's not copied into the built plugin.
  No Amplitude DLL is needed — the plugin's only compile-time game surface is string-based reflection; the vestigial
  `Amplitude.Mercury.Animation.dll` reference was dropped 2026-08-17.)

**No game installed?** `pwsh tools/fetch-refs.ps1` collects every reference DLL from public sources
(nuget.org, the BepInEx release, BepInEx's unstripped-Unity mirror) — this is what CI uses; it never overwrites
DLLs you already copied from the game. GitHub Actions builds + runs the full test suite on every push
(`.github/workflows/ci.yml`), along with every source-only guard — docs links, binding-catalog surface, hot path,
parse shape, editor source, and registry schema parity. Both halves now live in this repo, so parity needs no sibling
checkout. See
[Testing.md](Testing.md) for which guards run in which lane, and why the hook is not enough on its own.

Then:

```
dotnet build -c Release
```

and copy **both** `bin\Release\HumankindAssetFramework.dll` **and** `bin\Release\Haf.Schema.dll` → `<Humankind>\BepInEx\plugins\`
— or just run `bash tools/deploy-plugin.sh`, which copies both. `Haf.Schema.dll` (netstandard2.0) is the **shared model
schema** that `ModelDef` (editor) and `ModelEntry` (plugin) both inherit — the build produces it automatically via a
`ProjectReference`, but the plugin **won't load without it** (BepInEx reports the missing dependency), so the two ship
together. The editor package consumes the same tracked DLL from `editor/Plugins/Haf.Schema.dll`.

> **Refresh the tracked editor DLL when `Haf.Schema` changes.** The editor compiles against the DLL under `editor/`, not
> the source project, so a schema change is invisible on the editor side until that in-repo artifact is refreshed — and the failure
> looks like a compile error naming a type that plainly does exist (`'PackValidator' does not contain a definition for
> 'ValidatePack'`), which reads as *your* mistake rather than a stale binary. Hit exactly this on 2026-08-23 adding the
> wrapper rules. If `tools/editor_compile_check.sh` reports a member that you can see in `Haf.Schema/`, copy the DLL
> before believing it:
>
> ```
> cp Haf.Schema/bin/Release/netstandard2.0/Haf.Schema.dll editor/Plugins/Haf.Schema.dll
> ```

## Blender (optional dependency)

**Blender** is needed for `.blend` import, **animated-model import**, **Strip parts**, and Reduce-to-tris decimation —
auto-detected under `Program Files`, or point the Factory Settings override / `EditorPrefs 'ENC.blenderPath'` at
`blender.exe`. Static GLB/OBJ/FBX bakes with neither Strip nor Reduce need **no** Blender: the GLB path uses the
self-contained packaged `editor/Tools~/glbconv/glbconv.exe` (no .NET install required, and its `Weld & simplify` option decimates without
Blender). A `dotnet glbconv.dll` fallback exists for local dev.

### Why Blender does the geometry work (design rationale)

The heavy geometry passes shell out to headless Blender (`editor/Tools~/prep_model.py`, `rig_anim.py`, `blend_export.py`)
rather than being written in C#. The scripts are Python, but **Python is only the remote control** — the actual
decimation/import/export executes inside Blender's C/C++ core:

- **Quadric edge-collapse decimation is hard to write well** (error quadrics, topology preservation, UV/normal
  attribute handling). Blender's Decimate modifier is a battle-tested implementation; the script that drives it is
  ~15 lines. The C# alternative that predates it — `glbconv`'s `Weld & simplify` vertex clustering — survives as the
  Blender-free fallback, but clustering averages UVs across seams (scrambles textured skins) and eats thin features,
  which is exactly why the COLLAPSE path was added.
- **Blender was already a hard dependency** for `.blend` conversion and the animated pipeline (armature slimming,
  clip baking, FBX export) — Strip/Reduce ride an install the modder already has for those.
- **Performance:** the language is irrelevant to speed here. The per-bake cost is Blender **process startup + model
  import/export round-trip** (measured: the vast majority of a ~33s prep pass on a 13.6 MB model), not execution.
  That's why strip+reduce were merged into ONE Blender session (one startup, one round-trip, ~24% faster) instead of
  optimizing any code.
- **Future (package-readiness):** the static pipeline could go fully C# — an in-process quadric simplifier (e.g. a
  UnityMeshSimplifier-class library) plus node-name filtering inside `glbconv` would eliminate both the Blender
  dependency for static models *and* the round-trip cost. Blender would remain only for what genuinely needs a DCC:
  `.blend` files and animation. Tracked in `Framework-Review.md`.

## Tests

```
dotnet test Tests/HumankindAssetFramework.Tests.csproj -c Release
```

An xUnit suite (net471) over the **pure registry/parse/era layer** — the half that touches no live-game reflection,
and where the bugs have historically been. Needs the same gitignored `References\` DLLs as the plugin build (mirrored
into the test bin). Full detail — what's covered, what's deliberately out of scope and why, how to add a test — is in
**[Testing.md](Testing.md)**.

## Packaging a player release

`ENCReload/Tools/package-release.sh` builds the **player download**: the Humankind mod and the HAF plugin that
renders its custom units, in one reproducible artifact.

```
bash Tools/package-release.sh              # -> Distribution/release/ENCReload-<ver>.zip
bash Tools/package-release.sh --no-zip     # stage only (fast, for inspection)
bash Tools/package-release.sh --haf <dir>  # explicit plugin checkout
```

**Two numbered folders, because the halves install to different roots** — the mod is a Humankind *Community*
module, the plugin is a *BepInEx* DLL, and no single "extract here" covers both:

| In the zip | Goes to |
|---|---|
| `1_Humankind_game_folder/BepInEx/` | the Humankind install (the folder with `Humankind.exe`) |
| `2_Community_mods_folder/ENCReload.<guid>.<ver>/` | the Community folder the player's other mods are in |

`READ_ME_FIRST.txt` is generated from a **tracked** template (`Tools/release/`) with the version and module GUID
substituted, so the shipped instructions cannot drift from what shipped. The plugin is **built by the script**,
never collected from wherever a DLL happened to be lying — the hand-made v0.1.0 zip ended up 178 commits behind
master exactly that way.

**The stale-bundle guard is why this is a script and not a checklist.** `pack.json`'s `skel`/`atlas`/`clip` GUIDs
point *into* the asset bundle, so a re-bake after the last mod build produces a pair that looks fine and is
broken — *"waiting for leaves to load…"* forever, or a scrambled texture from a mismatched mesh/atlas pair. The
script refuses to package when any baked asset (or `pack.json`) is newer than the bundle: the same rule the
District Factory's health check already applies at authoring time, moved to where it would otherwise reach a
player. It excludes the bake-test fixtures by the same three prefixes the delete guard uses
(`__feat_` / `__smoketest__` / `__convgate__`) — a leftover fixture is newer than everything and would otherwise
fail every package.

## Editor tooling — installable package in this repository

The Model Factory and other authoring windows live in [`editor/`](https://github.com/sswelm/HumankindAssetFramework/tree/master/editor) and install through Unity Package
Manager. They compile and run in the host Unity project; ENCReload is the reference consumer. Packaged Blender and
converter helpers live under `editor/Tools~`, and `HafPackageContext` supplies safe guest-project defaults and derives
the guest pack identity. The deleted `baker/` editor snapshot must not return: it was a drifting second source.

## What `baker/` still holds

Two live things that exist **only** here, and are built from here:

- **`baker/glbconv/`** — the single source of truth for the GLB→OBJ converter (2026-08-17). Build with
  `dotnet publish -c Release`, deploy the exe to `editor/Tools~/glbconv/`, and **A/B-diff the OBJ output
  before deploying** (procedure in `baker/glbconv/BUILD.md`).
- **`baker/reactor_silhouette.py`** — the headless-Blender district silhouette helper
  ([District-Dedicated-Visual.md](District-Dedicated-Visual.md)).
