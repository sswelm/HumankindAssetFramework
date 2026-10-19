# Vehicle Generate acceptance

This records the acceptance work following PR #188, against merged commit `70dc263`.
The Generate default remains Blender. Phase (h) requires the user's visual and in-game check.
Moving tread/conveyor support (d6) remains deferred.

## Native JPEG-alpha support (October 19, 2026)

C# Generate now retains a source JPEG when a material reads its alpha. The glTF specification defines JPEG alpha
as 1; the material's alpha factor, cutoff, vertex colours, UVs and sampler remain unchanged. Blender instead writes
that image as PNG. Espana no longer needs that image-encoding fallback. The earlier dated results below record
its original fallback status.
The matching native rig-preparation policy is also enabled and its cache revision is bumped.

The current 25-recipe backend comparison matches all outputs: 23 take C#, including Espana, and the two FBX
sources retain named Blender fallbacks. Existing historical goldens for `sns_dragon_split_fused` and Espana are
stale against the current source/recipe fingerprints. Their candidates are retained for review; the project's
goldens are not rewritten. This is separate from the successful current Blender/C# output comparison.


Acceptance requires exact decoded RGBA pixels for each changed PNG/JPEG pair. It then normalizes only those
verified image payloads, MIME types and resulting buffer offsets/lengths; every other JSON field and buffer byte
must match. Native counterexamples change RGB, alpha, image metadata and geometry. The original byte comparison
still applies when no image encoding changed. No Lab golden or source fingerprint is relaxed or rewritten.

The additional animated SDK smoke test is not a pass: a throwaway Espana wave variant reaches native rig
preparation, then fails in `Skeleton.FillBoneEntry`. An equivalent PNG control fails in the same place.
This separately tracked native rig/SDK issue leaves full bake and in-game acceptance outstanding.


## Automated acceptance

The October 18, 2026 native run completed with **zero failures** on all 25 saved recipes:

- **22 C# runs** matched Blender's complete GLBs, diagnostics, gun sidecars and Lab goldens.
- **Three named fallbacks** matched Blender and the goldens: `Bredley_Low` and `SKM_Ehrhardt_E_V_Games`
  have FBX sources; Espana requires JPEG-alpha to PNG re-encoding. These are not C# passes.
- Espana's refreshed golden and Global Hawk's first golden were captured from Blender, then matched
  a separate generation on both backend selections. The verified files were installed in the local
  modding project's `Tools/lab_golden`; those project files are separate from this package repository.
- All 52 recipe/source SHA-256 fingerprints still matched after generation. The installed goldens
  are exact copies of the verified captures, and the project's golden checksum guard passed.
- Seven native fixtures passed capture and independent verification. Sampled preview, ownership,
  route persistence and deliberate GLB/log/sidecar/scale counterexamples passed. The final repaired
  stale-baseline regression also completed with zero failures in a separate native Unity run.

The native harness runs in a separate licensed Unity 2021.3 project with Blender 5.1.2. It uses the
actual `VehicleLabWindow.GenerateHeadless` entry point on both backend selections, compares complete
GLB bytes, reads the written rig, compares ordered diagnostics and gun sidecars, and judges the Blender
reference against Lab goldens. Missing goldens are captured and then verified by a second generation.
The source models, saved recipes, authored outputs and editor backend preference are not changed.

To repeat the check against the local modding project:

```bash
HAF_KEEP_UNITY_GENERATE=1 bash tools/vehiclerig_unity_generate_drill.sh
```

The harness's stale-baseline regression now owns a small fixture instead of assuming that the user's
Espana golden stays stale. It captures and verifies the original fixture, changes its Spin degrees,
checks rejection and preservation of the old golden plus retention of a candidate, then verifies
the refreshed synthetic golden. Updating the real project's baseline must not break this test.

## Espana baseline review

The old Lab golden was captured on October 12. The source was re-fused on October 18; its adjacent
fusion report identifies 25 groups and the resulting fused names. The recipe and its saved backup
both use those names. The current recipe also contains later part geometry, role and flip-count edits.

| Input | October 12 golden | Current input SHA-256 prefix |
| --- | --- | --- |
| Recipe | `17dc10cbde359dd2` | `520cd585d24e6e99` |
| Source | `a76e332839275b76` | `170364a379ce29ef` |

All 27 recipe part names exist in the current source, with no duplicate recipe names. The source has
28 mesh nodes: the additional `Fused_E_Material2_4884` remains attached to Root by the existing
unassigned-part behavior. Three recipe parts are explicitly Ignore. No source or recipe edit is
required to refresh this baseline.

The old golden has 1,612 normalized summary lines, including many missing-part warnings. The current
candidate has 36 lines and no missing-part warnings: four bound meshes, two joints, 134,105 vertices
and 200,152 triangles. Its body checksum is `c17b547c31183c00`. These are the current fused inputs'
Blender results; they do not establish visual quality or C# support for Espana's JPEG-alpha export.

The old golden was backed up before replacement; its SHA-256 was
`7fdf1803526e15a2c0b26643dfcf281750f1a8eb632be4bfe0f4e3c1a7d564f8`.
The installed Espana capture has SHA-256
`06f379b4fe516543db242a2c5e4fa5a00a87354c89380651fc0418f6e8b40003`;
the installed Global Hawk capture has SHA-256
`461386b20cf7802ac2681dec14b8fdbc7ba29454aa7c442c156f1a461c0949fa`.
Both were captured by Unity and matched another full generation. The isolated harness reports `editor=unknown` because its
test assembly is outside a package checkout; the tested package commit is recorded above. Input
hashes and the snapshot checksum are still captured by `BakeGoldenFiles`.

## Visual and runtime acceptance still required

In Vehicle Lab, select **C# with fallback** per recipe, Generate, and check the reported actual backend.
Check every saved recipe's preview and its generated asset in game. The automated check covers file
and pose parity; the user still needs to judge appearance, placement and the model playing its roles.
Record the checked package commit and each model's result before changing the global default.

| Feature | Saved recipes to check closely | Observe |
| --- | --- | --- |
| Rowing and Furl | `khelandion_2`, `triconter_split` | Mirrored strokes, clean loop, raised sails while moving, folded/struck idle stance |
| Sail and flag holds | `1861_us_convederate_navy_ship_split_fused`, `salegs_revenge_split_fused`, `ss._great_eastern_transport_fever_2_mod`, `bgm-71_tow` | Correct Furl stance and holds throughout Spin |
| Deploy and Recoil | `m114_gun_only`, `sms_wespe` | Trail opening, gun elevation, recoil lead-in, kick and return where enabled |
| Wheels and static tracks | `jagdpanzer_iv_late_prod3`, `ozhiga_organ_gun_1`, `Ribauldequin_textured`, gatling-gun recipe | Wheel direction/speed, stable geometry, intended static tracks |
| Rotors | `bell_h-13_animation-ready`, `rah66`, `sikorsky_ilya_muromets_split` | Main/tail axes, continuous rotation, scale and placement |
| Wave | `dug-out_canoe` | Hull sway and loop continuity |
| Blender fallbacks | `Bredley_Low`, `SKM_Ehrhardt_E_V_Games`, Espana | Named fallback, complete generation and usable preview |
| Remaining hulls and aircraft | All other saved recipes, including `rq4_global_hawk` | Materials, winding, scale, placement and intended motion |

The seven synthetic native fixtures also exercise combinations absent from the saved recipes, including
folded flags and Deploy/Recoil together. They supplement the user's saved-model checks.

Visual preview sign-off: **pending**. In-game sign-off: **pending**. Phase (h): **pending**.
