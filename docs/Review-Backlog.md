# Review backlog — deferred findings from the 2026-07-19 two-round audit

The two-round adversarial review (5 parallel reviewers per round over both repos; see the 07-19
[Framework-Review](Framework-Review.md) row) fixed every HIGH-severity finding the same day. This file tracks what was
**found, verified real, and deliberately deferred** — so the list survives outside the session that produced it. Ranked
by when they'll bite.

## Needs a decision first

- **Gate the rest-fold on the `convertRig` flag?** The rest-normalization currently triggers on the *presence* of
  `pose.bones[...].location` fcurves regardless of the flag — so a legacy-path re-bake of a location-keyed source
  (every `deploy_convert.py` output: the howitzer) still routes through the fold, which contradicts the documented
  "flag off = byte-identical" contract. Escalated by the round-1 hard-fail fix: a legacy model with location keys
  **plus shape keys (glTF morph targets) now aborts the bake** instead of warning. Gating the fold on the flag fixes
  both, but changes what a howitzer re-bake produces (the shipped howitzer was baked WITH the fold and verified) —
  needs an in-game check after the change. Decision pending with the user.

## Worth fixing before the next model of the affected kind

- **`deploy_convert.py` recoil block (before the next artillery model):** (a) guaranteed `KeyError` when the tube's
  parent bone isn't named `barrel`/`cannon` — `src_w[cradle]` is dereferenced unguarded (the M114's naming masked it);
  (b) the RecoilArm "identity hold" keys are back-solved at `deploy_end`, so they're only identity if the tube's parent
  chain is static during the deploy — a tilting carriage displaces the tube through the whole deploy; (c) empty
  `ordered` (no bone matches barrel/cannon) crashes `max()` with no message; (d) dead `key_bone` helper misleads.
- ~~**Feature Test Tier-2 bypasses `ConfigFor`**~~ — FIXED 2026-07-19: the animated fixtures now clone the registry
  entry and route through `ModelFactoryWindow.ConfigFor` like the smoke test, so `convertRig`/rotation/keep-flags all
  carry and the soldier is exercised on the conversion pipeline it actually ships on.
- **Unify the delete-first suffix lists across bake paths** — re-baking a model on the *other* path (animated↔static)
  orphans the previous path's outputs in shipped Resources (`_Clips`/`_ClipsPoseData` or `_ModelMesh`/`_Mat`/prefab):
  bundle bloat + a stale ClipCollection referencing a dead skeleton GUID.
- **District axis has no session re-arm** — `distFxManager`/private leaves cached from session 1 are reused in a second
  game of the same app run (same class of bug as the fixed model-axis latch). Needs its own careful pass on the
  leaf/manager lifecycle.
- **Plugin perf pass (late-game GC stutter):** per-pawn hook allocates closures/strings for every pawn incl. vanilla
  (`"Pose"+i`, ctx-capturing lambda, GetMember boxing); `ProcessEngineAudio` LINQ + `ProcessFireQueues` closures run
  per-frame before their throttles/early-outs; `TickOne` allocates an array + re-sets 7 material textures every frame.
  Needs deliberate hot-path work with in-game verification — do NOT rush.
- **Plugin unbounded per-instance dictionaries** — `engineLastPos`/`engineMoving`/`loopHoldUntil`/`customSources`/
  `deployLastPos`/`deployMoveState` never pruned: slow leak + stale first-poll reads when a new game reuses ids.

## Quality-of-life / lower risk

- **RetextureWindow Apply-without-Edit** writes engineSound/tints unconditionally from the form while preserving
  unbrowsed files — typing a name without pressing Edit silently resets that entry's sound/tint. Also Apply can create
  a duplicate `Retex_` entry for a pawn that already has a model entry (two entries, same pawn, undefined winner).
- **TechTreeWindow**: `_pending.Clear()` after a partial save discards skipped edits; `_pending` not serialized —
  every domain reload silently drops staged, unsaved edits; stale `_dragNode` after releasing outside the canvas
  teleports the node on the next drag.
- **ProjectileBaker**: bakes sprite donors (null mesh → invisible munition) with a success message, and Dump auto-fills
  the donor field even on a ✗ verdict; invalid impact-donor GUID silently ignored; muzzle swapped beyond the tooltip's
  documented scope; `ApplyTint` wipes the clipboard (kills the bake→tint→paste-GUID workflow).
- **PropBaker**: `FindType` full-AppDomain scan per repaint (editor sluggish while open — cache it); unchecked
  Amplitude GUIDs can write zero-GUIDs into the MeshCollection/fragment (silent "mammoth fallback" — guard like
  ProjectileBaker line ~294 does).
- **DatabaseBrowser**: the embedded-inspector catch swallows `ExitGUIException` — rethrow it (object pickers break).
- **Animated multi-material albedos**: `LoadOrderedAlbedos` drops no-`map_Kd` materials (index shift → wrong rects) and
  can't load `.tga` (red placeholder) — the static path handles both.
- **Regex-fallback parser drift** (plugin): overrides-array objects parsed as models when `models` is empty; count
  truncation via min(pd,skel,atlas); early-entry key omission misaligns later entries; resourceName default differs.
- **Misc small:** registry Save wipes hand-edited pack wrapper metadata (matters when a second pack author exists);
  Lab bakes a brand-new never-baked entry with default model fields (Factory→Lab handoff carries only name/file/pawn);
  Browse's auto-set `animUnitFix` announcement is discarded by the ownership merge for existing entries; case-sensitive
  Upsert/Remove matching (case-only rename → twin entries); `atlasGuid` never validated; `_ClipsPoseData.bytes` missing
  from the E5 rollback + Feature-Test cleanup lists; ConversionGateTest litmus synthesis sequential `ReadToEnd`
  pipe-deadlock pattern; texture leaks on bake failure paths; corrupt-registry error-spam from per-OnGUI `Load()` in
  Retexture/Sound windows; SoundWindow `ParseWav` (editor twin of the fixed plugin `LoadWav`) still lacks the negative
  chunk-size guard; parity script false-PASS shapes (empty N/R sets; awk section extraction); no-op root collapse is
  dead code post-rebake (every bone gets keyed by the visual rebake); multi-armature sources mis-convert silently;
  `blend_export.py` repoints packed images it shouldn't; prep_model strip matches object names only (not mesh-data
  names, unlike deploy_convert); AtlasDebug likely double-converts in a Linear-color-space project; RefreshList comment
  contradicts the settled Factory-lists-all design; 3-strike registry give-up latches per-process ("this session" log
  text is wrong); Hk_AudioTrace postfix unguarded + per-event string scans; 4u fire-radius / 3u deploy-match adjacency.

## Verified clean (don't re-litigate without new evidence)

GUID nibble-swap encoding + keep-GUID re-bake; registry corrupt-guard/atomic-write/backup lifecycle; two-window
ownership merge (both directions, post-fix); Harmony patch exception discipline; cross-thread sample locking +
ConcurrentQueue handoff; deploy ramp math; join/decimate + albedo-extraction blocks; frame clamping; noise-filter
re-entrancy; district bake+registry editor flow; Plugin.cs config wiring.
