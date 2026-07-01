# ENC Access Proof — Universal custom-3D-model injection for Humankind

A Humankind BepInEx (5 / Mono) plugin + Unity editor tool that **renders arbitrary custom 3D models on live units** —
no executable patching, and **no per-model code**. What began as a single proof (a procedural zeppelin replacing a
unit's mesh) is now the **Universal Model Factory**: bake any model in the editor, and a data-driven runtime injects it
onto its target unit, driven entirely by a JSON registry.

Three models ship proven in-game — a **Zeppelin**, an **LCAC Hovercraft**, and a **USS Zumwalt stealth cruiser** (first
textured + naval-combat unit) — each added with **zero new code**.

## Architecture (data-driven, zero per-model code)
**Editor — the Universal Model Factory** (`baker/`, menu *Tools > Universal Model Factory*):
- `ModelFactoryWindow` — pick a target unit (pawn) + a model file, set rotation / position / size / normals, Bake.
- `UniversalBaker` — imports the model and bakes it into an Amplitude `Skeleton` + atlas using the proven vehicle rig.
- `ModelRegistry` — writes `enc_models.json` (into the game's `BepInEx/config`) mapping each baked model → its pawn.
- **Formats:** GLB / glTF / OBJ / FBX, and **`.blend`** (auto-converted via an installed Blender — zero config; it
  locates `blender.exe` automatically). Textures embed for modern Principled-BSDF materials; very old materials may
  export untextured (supply the albedo manually).

**Runtime — `UniversalInject`** (`Patches/UniversalInjectPatch.cs`): one patch handles any number of models.
- Reads `enc_models.json` and registers every baked skeleton.
- On each unit's `AddOn.Load`, repoints the matching pawn onto its skeleton via **self-discovery** — reads the host's
  body-mesh name, renames ours to match, resolves the mesh index. The unit keeps its real bones / animation / material.
- **Texture isolation:** clones the host `FxOutputLayer` per model so our skin never bleeds onto a vanilla unit sharing
  that layer, and neutralizes the host's overlay maps (`_NormalMap`/`_ColorMask`/…) so only our albedo shows.

Adding a model = bake in the Factory → the registry updates → the plugin picks it up on next launch. No new code.

## The original insight (still the foundation)
Credit: CalmBreakfast — **do not inject a custom skeleton** (it silently hangs the GPU skinning compute). Keep the
unit's REAL skeleton and only swap the **mesh** it draws. The Factory generalizes this: it bakes a deep-cloned vehicle
skeleton as a real asset per model, and the runtime repoints the host pawn onto it. A custom **texture** rides along via
the material's `_MainTex` on the model's private output-layer clone.

## Models shipped
See [**CREDITS.md**](CREDITS.md) for authors + licenses. Models carry their own license (CC-BY requires attribution);
**model files aren't committed** — download each per its license into `Assets/Resources/<name>/` and bake.

## Config & registry
- **Model registry:** `<Humankind>\BepInEx\config\enc_models.json` — written by the Factory, read by the plugin.
- **Plugin config:** `<Humankind>\BepInEx\config\community.humankind.encaccessproof.cfg` — feedback-window key (F8), etc.

## Feedback window
Press **F8** in-game → a draggable window shows live scan results (registry counts + matching assets). Auto-scans on load.

## Documentation
Full write-ups in [`docs/`](docs/):
- [**Scaling-ManyModels-And-Scoping.md**](docs/Scaling-ManyModels-And-Scoping.md) — **start here.** The Universal Model
  Factory end to end: the data-driven architecture, per-model recipes (hovercraft / zeppelin / stealth cruiser), texture
  scoping (the `FxOutputLayer` clone) + overlay-map neutralization, the `.blend` import path, and the hard-won debugging
  lessons (which asset reflects rotation, the albedo name-scan bug, verifying deploys reach the game).
- [**FBX-to-Humankind-Pipeline.md**](docs/FBX-to-Humankind-Pipeline.md) — *why it works* end to end + the baking steps,
  with a blueprint for a Unity package.
- [**Custom3DInjection-Spec.md**](docs/Custom3DInjection-Spec.md) — the decompiled pipeline (AnimationManager /
  MeshCollection / Skeleton / PawnManager / AddOn / FragmentEntry) and how each blocker fell.
- [**Custom3DModels-Findings-Shareable.md**](docs/Custom3DModels-Findings-Shareable.md) — shareable findings & plan.
- [**UnitPreview-Findings.md**](docs/UnitPreview-Findings.md) — investigation log (data-mod limits, the BepInEx route).

## Toward a Unity package
The goal is to ship the Factory as a **distributable Unity package** any Humankind modder can use. Remaining gaps (docs
§9): decouple the hardcoded paths (game / BepInEx config / dotnet / Blender) into settings, neutral naming (drop "ENC"),
package scaffolding (`package.json`, README, LICENSE), and optionally a Unity-native glTF importer instead of the
bundled `dotnet` converter. The editor scripts are mirrored in `baker/` (the ENCReload mod repo tracks only its
`Databases`).

## Build (plugin)
Needs only the .NET SDK. All DLLs are local (BepInEx isn't on nuget.org — same setup as GUI-Tools/shakee).

1. Create a `References\` folder next to the `.csproj` and copy in (from your install):
   - `<Humankind>\BepInEx\core\`: `BepInEx.dll`, `0Harmony.dll`
   - `<Humankind>\Humankind_Data\Managed\`: `UnityEngine.dll`, `UnityEngine.CoreModule.dll`,
     `UnityEngine.IMGUIModule.dll`, `UnityEngine.InputLegacyModule.dll`, `Amplitude.Mercury.Animation.dll`
     *(`UnityEngine.dll` — the monolithic facade — is required because BepInEx's `BaseUnityPlugin : MonoBehaviour`
     resolves through it.)*
2. `dotnet build -c Release`
3. Copy `bin\Release\ENCAccessProof.dll` → `<Humankind>\BepInEx\plugins\`
4. Launch HK, load a save, press **F8** (and/or check `BepInEx\LogOutput.log` for `[ENCProof]` / `[Uni]` lines).

## Dependencies
- BepInEx 5 + HarmonyX + Unity + Amplitude — all via the gitignored `References\` folder (copied from your
  install; not redistributable). `Microsoft.NETFramework.ReferenceAssemblies` (NuGet) only enables the net471 build.
- **Blender** — optional, only for `.blend` import (auto-detected). The GLB path uses a bundled `dotnet` converter.
