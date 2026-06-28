# ENC Access Proof — first BepInEx mod

A minimal Humankind BepInEx (5 / Mono) plugin that **proves runtime access to a mod's assets**, with a
**config file** and an **in-game feedback window**. It's the foundation for the larger 3D-asset-injection plugin.

## What it proves
1. The plugin **loads** under BepInEx.
2. A Harmony hook **fires in-game** (postfix on `AnimationManager.AnimationResolveDependencies`, where the engine
   loads its content).
3. It can **read the live registry** (`AnimationManager.Content.MeshCollections` etc.).
4. It can **reach the configured mod's own assets** — scans the `PresentationPawnDefinition` database for entries
   matching `AssetNameFilter` (e.g. finds `Era5_Common_Zeppelins_01` for ENC).

## Config file
Auto-created at `<Humankind>\BepInEx\config\community.humankind.encaccessproof.cfg`:
- `TargetMod` (default `ENCReload`) — the mod whose assets to access (label).
- `AssetNameFilter` (default `Zeppelin`) — substring that identifies that mod's assets.
- `ToggleWindowKey` (default `F8`) — opens/closes the feedback window.

## Feedback window
Press **F8** in-game → a draggable window shows the live scan results (registry counts + matching assets) with
**Re-scan** / **Clear** buttons. Auto-scans once when a game loads.

## Build
Needs only the .NET SDK. All DLLs are local (BepInEx isn't on nuget.org — same setup as GUI-Tools/shakee).

1. Create a `References\` folder next to the `.csproj` and copy in (from your install):
   - `<Humankind>\BepInEx\core\`: `BepInEx.dll`, `0Harmony.dll`
   - `<Humankind>\Humankind_Data\Managed\`: `UnityEngine.dll`, `UnityEngine.CoreModule.dll`,
     `UnityEngine.IMGUIModule.dll`, `UnityEngine.InputLegacyModule.dll`, `Amplitude.Mercury.Animation.dll`
     *(`UnityEngine.dll` — the monolithic facade — is required because BepInEx's `BaseUnityPlugin : MonoBehaviour`
     resolves through it.)*
2. `dotnet build -c Release`
3. Copy `bin\Release\ENCAccessProof.dll` → `<Humankind>\BepInEx\plugins\`
4. Launch HK, load a save, press **F8** (and/or check `BepInEx\LogOutput.log` for `[ENCProof]` lines).

## Dependencies
- BepInEx 5 + HarmonyX + Unity + Amplitude — all via the gitignored `References\` folder (copied from your
  install; not redistributable). `Microsoft.NETFramework.ReferenceAssemblies` (NuGet) only enables the net471 build.
