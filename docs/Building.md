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
(`.github/workflows/ci.yml`).

Then:

```
dotnet build -c Release
```

and copy **both** `bin\Release\HumankindAssetFramework.dll` **and** `bin\Release\Haf.Schema.dll` → `<Humankind>\BepInEx\plugins\`
— or just run `bash tools/deploy-plugin.sh`, which copies both. `Haf.Schema.dll` (netstandard2.0) is the **shared model
schema** that `ModelDef` (editor) and `ModelEntry` (plugin) both inherit — the build produces it automatically via a
`ProjectReference`, but the plugin **won't load without it** (BepInEx reports the missing dependency), so the two ship
together. The editor half consumes the same DLL from `ENCReload/Assets/Plugins/HafSchema/Haf.Schema.dll` (drop it there
after a build, like the other managed plugins in `Assets/Plugins`).

## Blender (optional dependency)

**Blender** is needed for `.blend` import, **animated-model import**, **Strip parts**, and Reduce-to-tris decimation —
auto-detected under `Program Files`, or point the Factory Settings override / `EditorPrefs 'ENC.blenderPath'` at
`blender.exe`. Static GLB/OBJ/FBX bakes with neither Strip nor Reduce need **no** Blender: the GLB path uses the
self-contained `Tools/glbconv/glbconv.exe` (no .NET install required, and its `Weld & simplify` option decimates without
Blender). A `dotnet glbconv.dll` fallback exists for local dev.

### Why Blender does the geometry work (design rationale)

The heavy geometry passes shell out to headless Blender (`Tools/prep_model.py`, `rig_anim.py`, `blend_export.py`)
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

## Editor tooling — in ENCReload, not here

The Model Factory and the other authoring windows live — and are edited, compiled, and run — **only in the
ENCReload Unity project** (`Assets/Scripts/Editor/`, git-tracked there since 2026-07-03). To get the
**Tools ▸ HAF ▸ Model Factory** window, use that project. There is no copy of them in this repo: the stale
reference snapshot that used to sit in `baker/` was **deleted on 2026-08-21** (~7,000 lines) because it silently
omitted fields the plugin reads, so anything baked from it produced quietly wrong packs — reasoning in
`baker/README.md` and [Decisions.md](Decisions.md).

## What `baker/` still holds

Two live things that exist **only** here, and are built from here:

- **`baker/glbconv/`** — the single source of truth for the GLB→OBJ converter (2026-08-17). Build with
  `dotnet publish -c Release`, deploy the exe to `<ENCReload>/Tools/glbconv/`, and **A/B-diff the OBJ output
  before deploying** (procedure in ENCReload's `Tools/glbconv/BUILD.md`).
- **`baker/reactor_silhouette.py`** — the headless-Blender district silhouette helper
  ([District-Dedicated-Visual.md](District-Dedicated-Visual.md)).
