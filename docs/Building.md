# Building the plugin

For adopters who just want to *use* the Factory, no build is needed — grab a released `ENCAccessProof.dll`. Build only if
you're changing the plugin.

## Plugin (`ENCAccessProof.dll`)

Needs the **.NET SDK**. Put a `References\` folder next to the `.csproj` containing:

- `BepInEx.dll` + `0Harmony.dll` — from `<Humankind>\BepInEx\core\`
- `UnityEngine*.dll` + `Amplitude.Mercury.Animation.dll` + `Newtonsoft.Json.dll` — from `<Humankind>\Humankind_Data\Managed\`
  (Newtonsoft is provided by the game at runtime — used for robust registry parsing, since `UnityEngine.JsonUtility`
  silently returns empty in the game's Mono runtime; `Private=false`, so it's not copied into the built plugin)

Then:

```
dotnet build -c Release
```

and copy `bin\Release\ENCAccessProof.dll` → `<Humankind>\BepInEx\plugins\`.

## Blender (optional dependency)

**Blender** is needed for `.blend` import, **animated-model import**, and Reduce-to-tris decimation — auto-detected under
`Program Files`, or point the Factory Settings override / `EditorPrefs 'ENC.blenderPath'` at `blender.exe`. Static
GLB/OBJ/FBX bakes need **no** Blender: the GLB path uses the self-contained `Tools/glbconv/glbconv.exe` (no .NET install
required, and its `Convert grid` option decimates without Blender). A `dotnet glbconv.dll` fallback exists for local dev.

## Editor tooling (`baker/`)

The Universal Model Factory editor scripts are mirrored in `baker/` (the live copies compile inside the ENCReload Unity
project, which tracks only its `Databases` in git). Drop them into a Unity project that has the Humankind SDK to get the
**Tools ▸ Universal Model Factory** window.
