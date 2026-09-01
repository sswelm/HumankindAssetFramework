# Troubleshooting HAF

Start here when you know the **symptom**, but not which HAF subsystem owns it. This page is a router: it points to
the maintained diagnosis and fix instead of copying detailed recipes that would drift from their source.

## Start with evidence, not settings

Before changing a bake or registry field, check these surfaces in order:

| Evidence | What it tells you |
|---|---|
| **Unity Console** immediately after Save/Bake | Whether authoring succeeded, which helper/path was used, and the first actionable failure. |
| **Ship Status** (`Tools ▸ HAF ▸ Ship Status`) | Whether the baked assets or registry are newer than the bundle the game is actually loading. |
| **F8 → Smoke Test** in-game | Whether HAF loaded, its hooks resolved, the registry parsed, assets registered, and live pawns/districts use them. |
| `BepInEx/config/haf_load_report.txt` | Which packs loaded, their order, skipped dependencies, conflicts, declared overrides, and model counts. |
| `BepInEx/LogOutput.log` | The detailed runtime failure and the model, pawn, hook, or asset it belongs to. |
| `BepInEx/config/haf_bindings_report.txt` | Whether a Humankind update renamed a reflected type/member that HAF needs. |

The most common wasted cycle is editing the model when the game is still loading an old bundle. Check Ship Status
before changing geometry.

## Installation and authoring tools

| Symptom | Go to |
|---|---|
| Mod Editor stops at **Application Version** with `There is an error in XML document (2, 10)` | [Repair the Mod Editor `version.xml`](Mod-Editor-Version-XML-Recovery.md). A project path in the numeric `Build` field is the known cause; do not upgrade Unity to compensate. |
| Package Manager shows HAF, but **`Tools ▸ HAF` is missing** or Unity reports compile errors | [Installation → What a correct install looks like](Installation.md#what-a-correct-install-looks-like), then [Updating the tools](Installation.md#updating-the-tools). |
| HAF cannot find Blender or a conversion helper | [Installation → Blender helpers](Installation.md#blender-helpers) and [Factory settings](Factory-Manual.md#settings--game--blender-path-foldout-at-the-top). Packaged scripts live under `Tools~`; only `blender.exe` is external. |
| Bake fails, produces zero vertices, or reports an import/conversion error | [Factory Manual → Troubleshooting](Factory-Manual.md#8-troubleshooting). Keep the **first** Console error; later Unity errors are often consequences. |
| A model is invisible, see-through, microscopic, enormous, grey, or oriented incorrectly | [Factory Manual → Troubleshooting](Factory-Manual.md#8-troubleshooting) owns the symptom table and corrective bake fields. |
| Vehicle Lab animation works, but the baked vehicle is flat, all black, or has scrambled material regions | [Textures → Failure catalog](Textures.md#failure-catalog--match-your-symptom). Use Auto/Multi and make a control bake with **Reduce to ~tris = 0** before editing textures. |
| The preview or registry changed, but the game did not | [Ship Status → The trap it closes](Ship-Status.md#the-trap-it-closes) and [Factory Manual → rebuild the mod](Factory-Manual.md#6-after-baking-rebuild-the-mod-dont-skip-this). |

## Textures and materials

Use [Textures → Failure catalog](Textures.md#failure-catalog--match-your-symptom) for pale/washed output, white
parts, silver or collaged panels, blurry atlases, missing skins, non-[0,1] UVs, poisoned previews, and magenta UI
images. That page also distinguishes a **baked atlas problem** from a runtime PNG re-skin problem.

Treat the three preview surfaces differently: Vehicle Lab checks rigging (Checker overrides materials), the post-Bake
Factory preview checks atlas mapping but uses editor lighting, and the game is authoritative for final appearance.

If the model is otherwise correct, do not change skeleton or scale fields to fix a texture symptom.

## Animation and skeletons

| Symptom | Go to |
|---|---|
| Model is rigid, rests in the wrong pose, pauses each loop, wobbles, or parts fly apart | [Animation Pitfalls → Symptom index](Animation-Pitfalls.md#symptom-index). |
| Converted rigid-part vehicle has crossed legs, wrong pivots, or no authored wheel/turret motion | [Factory Manual → conversion troubleshooting map](Factory-Manual.md#165-troubleshooting-map-symptoms--cause). |
| Borrowed donor clip plays at the wrong height/axis, freezes, or moves the wrong bones | [Donor Clip Flight → Failure catalog](Donor-Clip-Flight.md#failure-catalog-what-each-symptom-means). |
| The first pawn uses the donor/wrong skeleton or animation works only after a reload | [Animated Runtime → wrong-skeleton net](Animated-Runtime.md#8-the-wrong-skeleton-net-and-why-it-must-be-armed-before-the-first-pawn). |

Animation failures are usually contract failures, not random import failures: clip selection, bind pose, unit scale,
bone order, or the default rotation-only bake. Follow the linked catalog before adding runtime compensation.

## Packs, conflicts, and “nothing loaded”

Open `haf_load_report.txt` first, then use [Multi-Mod → The load report](Multi-Mod.md#the-load-report).

| Report line / symptom | Meaning |
|---|---|
| Pack absent | Wrong `haf_packs` location/name, unreadable JSON, or the game is using another install. |
| `SKIPPED` dependency | A required `modId` did not load; fix the dependency or its identity first. |
| Duplicate `modId` | Two packs claim one identity; the later pack is rejected. |
| Undeclared conflict | Two packs target one `pawnDescription`; first-loaded wins until an override is declared. |
| New field appears ignored | Compare the pack's `schemaVersion` with the implemented version printed in the report; update HAF if the pack is newer. |

Do not debug a skipped pack's model assets: none of its entries reached registration.

## Feature-specific symptoms

| Symptom | Owner |
|---|---|
| Formation count/layout is unchanged, duplicated, or capped | [Formations → Troubleshooting](Formations.md#troubleshooting-read-bepinexlogoutputlog). |
| District model or texture never appears | [District Visuals](District-Visuals.md) for the isolate path; [District Dedicated Visual](District-Dedicated-Visual.md) for scoped/strategic footprints. Run the F8 Smoke Test because it reads both district ledgers. |
| Unit/creature WAV does not play | [Factory Manual → Unit sounds](Factory-Manual.md#13-unit-sounds--engine-audio--the-sound-catalog) and [custom sound files](Factory-Manual.md#14-custom-sound-files--per-clip-volume--the-sound-studio-window). |
| A game-wide Wwise override does not apply | [Game Sound Lab](Game-Sound-Lab.md); audition the exact event name from F8 before authoring the override. |
| Frame time grows or the game stutters | [Performance → When a number grows](Performance.md#4-when-a-number-grows); diagnose the named F8 bucket rather than estimating from code. |
| Several unrelated features break after a Humankind update | [Testing → Headless binding drift check](Testing.md#headless-binding-drift-check-toolscheck-bindingssh--for-game-updates), then inspect `haf_bindings_report.txt`. |

## Still stuck: the minimum diagnostic packet

When reporting a problem, include:

1. The **first** Unity Console or `LogOutput.log` error, with its preceding HAF line.
2. The relevant `haf_load_report.txt` pack block.
3. The F8 Smoke Test summary and, after a game update, the missing lines from `haf_bindings_report.txt`.
4. Pack `modId`, model resource name, target `pawnDescription`, and whether the entry is static or animated.
5. What changed since the last known-good result: package update, re-bake, mod rebuild, registry-only edit, or game update.

That packet distinguishes authoring, packaging, load resolution, engine binding, and live injection without asking
someone to reproduce the entire project.
