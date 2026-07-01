# ENC Access Proof — Universal custom-3D-model injection for Humankind

Inject **any** custom 3D model onto **any** live Humankind unit — correct geometry, correct texture, no executable
patching, and **zero per-model code**. Bake a model in the Unity editor tool; a data-driven BepInEx plugin renders it
in-game, driven entirely by a JSON registry. What started as a single procedural zeppelin is now the **Universal Model
Factory**.

## What works (proven in-game)
- **Three models live**, each added with no new code: a **Zeppelin**, an **LCAC Hovercraft**, and a fully-textured
  **USS Zumwalt stealth cruiser** (first textured naval-combat unit) — correct orientation, correct skin, sitting at
  the waterline.
- **Any format in:** GLB / glTF / OBJ / FBX, and **`.blend`** (auto-converted via an auto-detected Blender install).
- **Correct textures out of the box:** the GLB→OBJ converter flips V (glTF is V-top, OBJ/Unity V-bottom), so skins map
  right — the bug that made the Zumwalt's markings land on the superstructure, now fixed for every model.
- **Texture isolation:** each model gets a private `FxOutputLayer` clone, so its skin never bleeds onto a vanilla unit
  sharing that layer.
- **Add a model = bake it.** The Factory writes the registry; the plugin picks it up on next launch.

## How it works
**Editor — the Universal Model Factory** (`baker/`, *Tools ▸ Universal Model Factory*): pick a target unit + a model
file, set rotation / position / size / normals, **Bake**. `UniversalBaker` imports it and bakes an Amplitude
`Skeleton` + atlas on the proven vehicle rig; `ModelRegistry` writes `enc_models.json` into the game's `BepInEx/config`.

**Runtime — `UniversalInject`** (`Patches/`): one patch, any number of models. Reads the registry, registers each baked
skeleton, and on `AddOn.Load` repoints the matching pawn onto it by **self-discovery** (reads the host's body-mesh name,
renames ours to match). The unit keeps its real bones + animation; our skin rides on the private layer clone.

*Founding insight (credit: CalmBreakfast): never inject a custom skeleton — it hangs the GPU skinning. Keep the unit's
real skeleton and swap only the mesh it draws.*

## Models & licenses
Model files aren't committed — download each per its license into `Assets/Resources/<name>/` and bake. Authors +
licenses in [**CREDITS.md**](CREDITS.md) (CC-BY requires attribution).

## Config
- **Registry:** `<Humankind>\BepInEx\config\enc_models.json` (Factory writes, plugin reads).
- **Plugin cfg:** `…\community.humankind.encaccessproof.cfg` — F8 opens an in-game scan/feedback window.

## Docs
Full write-ups in [`docs/`](docs/) — **start with [Scaling-ManyModels-And-Scoping.md](docs/Scaling-ManyModels-And-Scoping.md)**
(architecture, per-model recipes, texture scoping, `.blend` import, and the full debugging log incl. the UV V-flip root
cause). Also: [FBX-to-Humankind-Pipeline.md](docs/FBX-to-Humankind-Pipeline.md), [Custom3DInjection-Spec.md](docs/Custom3DInjection-Spec.md)
(decompiled pipeline), [Custom3DModels-Findings-Shareable.md](docs/Custom3DModels-Findings-Shareable.md), [UnitPreview-Findings.md](docs/UnitPreview-Findings.md).

## Toward a Unity package
Goal: ship the Factory as a distributable Unity package. Remaining gaps: decouple hardcoded paths (game / BepInEx /
dotnet / Blender), neutral naming (drop "ENC"), package scaffolding, optional Unity-native glTF importer. Editor scripts
are mirrored in `baker/` (the ENCReload mod repo tracks only its `Databases`).

## Build (plugin)
Needs the .NET SDK. Put a `References\` folder next to the `.csproj` with `BepInEx.dll` + `0Harmony.dll` (from
`BepInEx\core\`) and `UnityEngine*.dll` + `Amplitude.Mercury.Animation.dll` (from `Humankind_Data\Managed\`), then
`dotnet build -c Release` and copy `bin\Release\ENCAccessProof.dll` → `<Humankind>\BepInEx\plugins\`. **Blender** is
only needed for `.blend` import (auto-detected); the GLB path uses the bundled `dotnet` converter (`Tools/glbconv`).
