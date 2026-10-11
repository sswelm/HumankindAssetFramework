# First C# animated slice (2026-10-11)

`BlenderFreeClipSlice.RunBatch` is an opt-in, reproducible vertical slice for a
processed rigged GLB. It uses `GlbReader` and `HafModelRig`, merges the skinned
parts into one `_ModelMesh`, writes a temporary prefab and a solid white atlas,
then bakes the game's `Skeleton` and `ClipCollection`. It checks the baked body
name, bone weights, frame and bone counts, and calls `ClipCollection.Load` on the
saved pose bytes. It does not change the Factory's production Blender route.

The current contract is intentionally narrow: one skin, no rigid mesh renderers,
the first animation at 24 fps, bone translation curves removed, and the longest
rest-pose axis scaled to 5 units. The solid atlas proves game asset plumbing; it
does not reproduce the source materials. The SDK requires one tangent per vertex
and discovers an `AnimationClip` as a subasset of a source GUID. A standalone
`.anim` produced an empty collection; the native clip container now works.

Run in Unity 2021.3.1f1 with this editor package:

```
Unity.exe -batchmode -nographics -quit -projectPath <project> \
  -executeMethod BlenderFreeClipSlice.RunBatch \
  -hafSliceInput <absolute processed-rig.glb> \
  -hafSliceReport <absolute report.txt>
```

On `AssaultInfantry/deploy_converted.glb` (1,218,360 bytes; four skinned mesh
parts; five source actions), the generated `deploy` collection passed with one
body mesh, 5,235 vertices, 4 SDK bones, 24 frames, and 1 second of duration.
The persisted pose stream was 1,296 bytes. The previously Blender-baked
`AssaultInfantry_Clips` has the same frame count, bone count, and duration, but
its pose stream is 576 bytes. The bytes and skeleton layout are **not** equivalent;
the C# route's rest transforms, translation encoding, and material/placement
handling still need visual comparison before production use.

| Stage | Measured wall time |
| --- | ---: |
| C# GLB read, rig build, single-body prep | 230 ms |
| C# through prefab asset save | 512 ms cumulative |
| C# through SDK skeleton bake | 743 ms cumulative |
| C# through SDK clip bake | 902 ms cumulative |
| Blender 5.1.2 `rig_anim.py` to FBX, same GLB and `deploy` action, target-tris off | 2,000–2,034 ms |

These are local single-run observations, not matched end-to-end benchmarks:
Unity startup is excluded from the C# phase timer, while Blender starts a fresh
process; Blender performs its own centering, grounding, and material work, and
the C# slice bakes a white atlas and the SDK assets. Do not infer a production
speedup or visual parity from the ratio.

The game's Mod Editor built an assetbundle containing the C# skeleton, atlas,
collection, and pose bytes. Deployment to the existing OneDrive Community folder
failed because its `1.3505` directory denied deletion. A temporary `1.3506` copy
and a test registry entry were made for the game check, then removed/restored.
Headless and Steam launches exited before HAF loaded the mod registry. A normal
interactive launch reached the main menu, where the existing plugin reported
`no registry session`, and exited before a save or pawn could be loaded. The
`haf_load_report.txt` remained unchanged. **In-game playback is unverified.**
The remaining acceptance step is to launch a game session with the test bundle,
see the test entry's collection resolve and animate on a pawn, and compare its
pose and placement with Blender's unit. Material atlas parity, clip selection,
state roles, decimation, and source-independent sizing remain outside this slice.

The static extraction cache now includes `BlenderPrep.CacheRevision` in its
fingerprint. Bump that revision whenever in-process prep output changes. The
existing `keepTexture` protection continues to preserve hand-edited extracted
files and warns rather than overwriting them.
