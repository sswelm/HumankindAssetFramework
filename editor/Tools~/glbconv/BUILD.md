# Building glbconv

`glbconv` is a small .NET 8 console app. **The single source of truth is the HumankindAssetFramework repo:
`baker/glbconv/` (`Program.cs` + `glbconv.csproj` + the pinned `SharpGLTF.Core.dll`).** There is deliberately NO
source copy in this repo — a `Program.cs.src` snapshot used to live here, and by 2026-08-16 the two copies had
split-brained: the snapshot alone had the T5 mirrored-winding fix, the HAF copy alone had the multi-tile UV warning,
and the deployed exe (rebuilt from the HAF copy that day) shipped with T5 REGRESSED — mirrored halves of symmetric
vehicles would wind inward and vanish under backface culling. Merged + fixed 2026-08-17; never reintroduce a second
source. (A bare `.cs` must never sit anywhere Unity scans — `Assets/` — which is why the tool lives outside it.)

The Factory runs the built **`glbconv.exe`** here (`Tools/glbconv/`), self-contained single-file, so no .NET install
is needed on the modder's machine.

## Rebuild

From the HumankindAssetFramework repo (the csproj carries the publish settings — single-file, self-contained,
compression on):

```sh
dotnet publish baker/glbconv/glbconv.csproj -c Release
# then copy baker/glbconv/bin/Release/net8.0/win-x64/publish/glbconv.exe over <ENCReload>/Tools/glbconv/glbconv.exe
```

**Do NOT add `-p:PublishTrimmed=true`.** SharpGLTF's JSON layer trips trim analysis (IL2026) and trimming
silently *changes* the OBJ/MTL output on some models (verified: 4 of 11 models differed under trimming).
`EnableCompressionInSingleFile` shrinks the exe ~68 MB → ~35 MB **losslessly** (the bundle unpacks at runtime),
so trimming isn't needed anyway.

**After any rebuild, A/B-verify before deploying:** run the old and new exe over the FactorySource models
(`<glb> <outdir> model 0` — faithful mode) and diff the OBJs; only the intended change may differ. The 2026-08-17
merge rebuild was verified byte-identical on 4 FactorySource models plus a synthetic mirrored-node model that
confirms the T5 winding swap fires (old: `f 4 5 6`, fixed: `f 4 6 5` on the mirrored node only). Also re-verify a
tiled-UV model (the Cobra camo) in-game — UV folding touches exactly the class of model the tiled-UV handling was
fixed for.

## SharpGLTF pin (reproducibility) — resolved 2026-07-12

Builds reference the **committed `SharpGLTF.Core.dll`** in `baker/glbconv/` (HintPath, not a PackageReference), so
rebuilds are reproducible. Historical note: the pre-2026-07-12 exe was built with an older, unrecorded SharpGLTF that
emitted **raw tiled UVs** (e.g. `U=19.8`); the pinned build pre-folds them into `[0,1)` (`U=0.8`). The baker folds
every UV per-vertex anyway (`u -= floor(u)`), so the two are functionally equivalent except at tile boundaries — on
the Cobra, **3 of 208,198** verts shift one tile (0.0014%, sub-pixel on a seam). Older exes are preserved in git
history.
