# Review backlog — deferred findings from the 2026-07-19 two-round audit

The two-round adversarial review (5 parallel reviewers per round over both repos; see the 07-19
[Framework-Review](Framework-Review.md) row) fixed every HIGH-severity finding the same day. This file tracks what was
**found, verified real, and deliberately deferred** — so the list survives outside the session that produced it. Ranked
by when they'll bite.

## Needs a decision first

- ~~**Gate the rest-fold on the `convertRig` flag?**~~ — DECIDED + IMPLEMENTED 2026-07-19: **split gating.** The
  destructive rest-fold (rest rewrite + visual rebake) is now conversion-path only (`_loc0 and convert_rig`) — a
  legacy model with location keys + shape keys no longer aborts, and legacy means *no rig manipulation*. The
  location-STRIP stays on BOTH paths deliberately: every verified legacy bake (drone, howitzer) went through it, and
  un-stripping risked re-introducing the drone's unscaled-translation wobble. Rationale: legacy rigs have a sane rest
  by definition, and for them the fold was a near-no-op (frame-0 pose ≈ rest) — so gating it off converges on the
  same output. **Bake-level verification DONE** (same day): smoke test 14/14 with the howitzer fresh-baked
  `animated-legacy` through the gated pipeline. **In-game verification DONE (2026-08-02)** — the howitzer checked out
  correctly after a real re-bake.

## From the 2026-08-22 critical review (confirmed, unfixed)

Every item below was re-verified in source during the review; the range's critical (the strike hold reusing a
stale aim marker) was fixed the same day and is not repeated here. Ranked by consequence.

- **`Make static…` bypasses the name-collision guard** (`ModelFactoryWindow.cs:948`). `nameCollides` gates
  `canBake` and Save settings only; the Make-static button sits in no disabled scope and calls
  `ModelRegistry.Upsert`, a blind `RemoveAll(name) + Add`. With the red "Not allowed" box on screen, one click
  destroys the colliding entry and orphans its baked assets — the exact data loss 898c732 was written to stop.
- **The Vehicle Lab's trail/gun/recoil dials are dead on rigged sources.** Eight sites in that block count roles
  from `parts`; every other section uses `ActiveParts`, and Generate uses `fast ? boneParts : parts`. On an
  `SKM_` source with the fast path on, the section reads "no trails marked" and every control is disabled, while
  Generate ships the defaults (35° spread, pivot 0.5, recoil 0).
- ~~**The trail-spread sign heuristic may test the wrong axis**~~ — **DRILLED AND DOWNGRADED 2026-08-22.** The
  headless drill (M114, 13 yaw angles) shows the shipped rig is **correct**: at yaw 0/90/180 the two trails take
  opposite signs and open to a ~102-unit spread. The critical does not reproduce. What the drill *did* confirm is
  a narrower fragility: at every yaw in between, both arms take the **same** sign and the spread collapses to ~12
  units — off-axis the test measures the arm's foreshortening in x rather than distance from the centreline. The
  live path is real (`model_rot` is applied and baked *before* the rig is built), but the dials exist precisely to
  square a model up, so it needs someone to leave a gun at an odd angle. Two rewrites were tried against the same
  harness and both scored **worse** (they failed at yaw 0, where the current rule passes), because the real
  off-axis fault is upstream: the arm's ends come from the dominant axis-aligned bbox extent, which mis-picks the
  ends of a diagonal arm. Fixed instead: the silence — an un-mirrored pair now warns, promoted to the Lab's status
  box. **Still open** (low priority, needs a diagonally-authored gun to matter): rotation-invariant arm-end
  extraction, after which the sign rule can be re-derived from the trails' own centreline.
- ~~**The live-pawn check is fed by the hook it is checking.**~~ — **FIXED 2026-08-22.** The smoke now samples an
  independent oracle (`CountLiveArmies()`, read from the presentation entity factory — a surface no HAF hook
  writes) alongside the registered-manager count. Zero managers while armies are live and entries are injected is
  a FAIL naming the consequence; the benign shapes (no armies; managers but no matching descriptor ids) produce a
  NOTE and a printed `0 live pawn(s) examined`, never a dropped clause. An unreadable oracle returns -1 and cannot
  pose as a confident zero. Five tests, mutation-drilled.

- **One report can still say PASS on nothing.** The bake-test verdict treats zero failures as success, so an
  all-skipped run writes `PASS — 0 passed` to `haf_bake_tests_report.txt`. (Two of the original three are fixed:
  the smoke's *live-pawn* clause and the *matched-but-never-repointed* misfiling — see the entries above. Still
  cosmetic-but-dishonest: the remaining coverage clauses, `SubPawnScene` / `LayersChecked` / `SeamsChecked` /
  `RolesChecked` / `SoundsChecked`, are suppressed at zero rather than printed.)

- ~~**The catalog gate cannot see the `CachedField(` family.**~~ — **FIXED 2026-08-22**, and it was worse than
  reported: teaching it `CachedField(`/`GF(` surfaced 19 uncatalogued names, and a second pass for nested calls
  (`GetMember(GetMember(x, "Inner"), "Outer")` only ever yielded *Inner*) surfaced 13 more — including
  **`FacingAngleOffset`**, the member the 08-21 review had named, which was never actually catalogued; its only
  mention in `GameBinding.cs` was the comment describing that review. Now catalogued with `TagAsAbilities` and
  bindcheck-validated. **Still open (low priority):** ~30 duck-typed reads over runtime-resolved types
  (`mat.GetType()`, `voBox.GetType()`, the skeleton buffer element at `Pose.cs:42`) are site-scoped allowlist
  entries with reasons, not catalog bindings — the functional ones among them still degrade silently on a game
  rename. Promoting them via the A6 `CachedDerived` mechanism (anchored on the type that produced the instance)
  is the real close-out.

- **The sub-pawn walk double-counts, so its coverage number can read better than complete.** A `PresentationUnit`
  reached twice during a battle (armies *and* battle units), and a squadron reachable both via the holder subtree
  and the air-formation `MainPawn` walk, are counted twice — the F8 panel showed `sub-pawn walk 56/46` on
  2026-08-22, i.e. ten duplicates against a superset oracle. The miss detection is set-based on instance ids so the
  verdict is sound, but the printed number is misleading and `ProcessEngineAudio` processes the duplicated pairs
  twice per poll. Fix: dedupe by `GetInstanceID()` before counting.
- ~~**Two gates have blind spots and neither is in CI.**~~ — **HOT-PATH HALF FIXED 2026-08-22**: the grep is
  case-insensitive with stand-alone word matching (the naive `-i` false-positived on `docs/Wonder-Spike.md`),
  verified in both directions, the three stale `(spike)` labels promoted (all three are documented shipped dials),
  and **both** source-only guards moved into CI beside the docs guard. **Still open:** `check-catalog.sh`'s
  extraction regex omits the `CachedField(` accessor family (16 call sites, 3 added by that range) — its literals
  are catalogued today, so the gate is green by luck rather than by proof.
- ~~**`PackTuning` dropped the `sv > 0f` guard.**~~ — **FIXED 2026-08-22.** Guard restored as `!(sv > 0f)` so NaN
  is rejected too, and it now WARNS with the pack, key and value instead of skipping in silence. No shipped pack
  was affected. More importantly the missing discipline was supplied: `PackTuningLegacyParityTests` keeps the
  pre-extraction loop verbatim as an oracle and compares over a 19-entry corpus — mutation-drilled, re-introducing
  the bug fails 6 tests. The remaining `PackTuning` gap from the review is unrelated and still open: the
  cross-pack conflict NOTE is keyed on exact `match` strings while the runtime matches by **substring**, so
  `"Tank"` in one pack and `"Tanks_01"` in another both apply (×0.36) with no note.
- ~~**`fireGuidQueue` is never drained on re-arm**, and the fence sees 137 of 549 statics.~~ — **FIXED
  2026-08-22.** The queue is drained by the re-arm sweep, and the fence is redrawn by intent rather than by
  "the type has `Clear()`": queues/bags are drained, arrays zeroed, `ConditionalWeakTable` forced to declare a
  lifetime. 27 previously-invisible statics are now annotated. Scalars remain outside on purpose (shape cannot
  distinguish a constant from a per-session latch) and `UnpolicedStaticCount()` reports how many, so the edge is
  measured. **Still open from the same review:** `footprintMaskInjected` is exactly that unpoliceable shape — a
  `static bool` latch that survives a session reset while `ResetDistrictSessionState` destroys the clone it
  guards, leaving the strategic-zoom footprint dead until a process restart. It needs a per-session reset by
  hand; the fence cannot find it for you.
- **`Hk_BattleHoldFire` fails closed** (`BattleTurnPatch.cs:343`): `ct == null ||` selects the hold branch, so a
  missing `creationTime` holds the attack every frame with no deadline — against the stated policy every sibling
  hold follows ("any failure = vanilla, never a stuck army").
- **A fourth hand-maintained field list is ungated**: Clone's `int[4]` GUID clears (11 such fields in `ModelDef`).
  `check_handlists.sh` already gates three lists of exactly this shape; this one is guarded by a comment.
- **`Plugin.Update` has no try/catch**, so a throwing poll skips the frame's own accounting — the meter reads
  healthier exactly when HAF is most broken. And `docs/Performance.md`'s "everything HAF did in an average
  frame" overstates the meter: 33 timing sites cover the Update fan-out, the pose hook and the district path,
  not the other ~36 Harmony hooks or `OnGUI` (which does a full reflection walk of the GPU budget per repaint).

## Worth fixing before the next model of the affected kind

- **A CONVERTED RIG'S CLIPS DON'T SHARE A FRAME WITH ITS REST POSE (measured 2026-08-22, the howitzer wheels)** —
  every clip `deploy_convert` produces for the M114 poses the model **90° rotated** from its own rest pose
  (rest bbox `(52.1, 135.7, 37.6)` vs `folded` `(41.7, 27.6, 119.3)`), the legacy clip additionally at **2× scale**;
  the baked skeleton then carries compensating scales (`howitzer:main` Local **2**, wheel BindPose **0.005**, where
  a Vehicle-Lab rig reads 1/1). Pawn-level features are blind to it — everything shipped today works — but
  **bone-level ones inherit a frame that disagrees with the geometry**, so authored bone motion (a wheel roll, and
  by extension any future bone-driven feature on a converted model) pivots wrongly and cannot be compensated
  reliably. Motion the SOURCE animates rides through fine (the T-62's wheels spin), which is why this went
  unnoticed for so long. **Acceptance test, offline, no bake and no game: `folded` at frame 1 must have the rest
  pose's bbox orientation.** Guarded by the existing conversion golden-master gate. Full write-up + the four
  offline verification recipes: [Animation-Pitfalls.md ▸ "Authoring INTO a converted rig"](Animation-Pitfalls.md).

- **ENTRY-STATE COHERENCE (user verdict 2026-07-26, tread-saga fallout: "this seems like a serious configuration
  bug")** — an entry's config lives in FOUR places (Factory window memory, Animation Lab memory, the DEPLOYED
  pack.json the editor reads as its registry, the project dual-write copy) and the reconciliation rules ambushed
  the user repeatedly in one afternoon: (a) a stale Factory Model-file field silently baked the WRONG MODEL (the
  translation-test cube overwrote a good Jagdpanzer bake); (b) animated→static downgrade is IMPOSSIBLE without
  Remove — the bake-time ownership rebase resurrects the saved animation config even after Reset, and the animated
  pipeline then hard-fails on an unrigged file; (c) "Reduce to ~tris (0 = off)" silently substituted 12,000 on the
  animated path for years (FIXED same day); (d) external registry edits are detected by the Lab (yellow banner)
  but not by the Factory. Proposed fixes, in impact order: ~~(1) Factory gets the Lab's outside-change banner +
  a bake-time confirm when its Model file differs from the registry's~~ **DONE + DRILLED 2026-08-18** (banner +
  explicit Reload-entry choice, coherence-aware cross-window nudge — a Backup-window restore now raises the banner
  instead of silently reloading — and the bake-time model-file confirm with both paths shown; plus the **SelectEntry
  funnel**: every selection change routes through one path, structurally retiring the 08-16..18 stale-window
  family. All five drills passed; drill 3 caught a real unreachable-banner defect — see CHANGELOG); ~~(2) a real animated→static path~~ **largely covered** by the "Make static…" button (strips the
  animation config from the saved registry; the offer-on-armature-less-failure variant remains nice-to-have);
  ~~(3) document (or collapse) the two-pack.json design~~ **COLLAPSED 2026-08-19**: the git-tracked project
  file is the single source; the deployed copy is a regenerated build artifact with hand-edit drift warnings
  and a one-time migration (districts/formations inherited it 2026-08-20 via the shared `SingleSourceRegistry`); ~~(4) audit
  remaining "label lies" like the tris slider~~ **DONE 2026-08-19** — swept both families mechanically
  (UI-field extraction diffed against every hand-list; every runtime/no-re-bake claim read against its code
  path). Hand-lists: the Factory rebase (34 fields), the Lab rebase (56) and the bake-config capture are all
  COMPLETE — zero UI-edited fields uncovered. Three findings, all fixed same day: MakeStatic left
  gunElevMax/gunElevAxis/animPhaseSpread uncleared (gunElev is runtime-applied — a made-static gun kept
  elevating: the cursed-leftover class MakeStatic exists to kill); the Save-settings status claimed Position
  offset/Size "apply on load" unconditionally (false for statics — now entry-type-conditional); Browse's
  animUnitFix auto-set is discarded by Save settings (animation-owned — the status now says so). The original
  tris-slider example was already clean (tooltip + bake log disclose the double-sided halving). ~~Residual risk
  is the MAINTENANCE-TRAP comments at each hand-list — no gate enforces them.~~ **Gated 2026-08-19**:
  `Tools/check_handlists.sh` (pre-push, drilled at birth on the planted combatZ omission) — the silent-reset
  class is structurally impossible now.

- ~~**`deploy_convert.py` recoil block**~~ — FIXED 2026-07-19: (a) the tube's parent is now sampled into `src_w` when
  its name isn't barrel/cannon (was a guaranteed `KeyError` on non-M114 naming); (b) the RecoilArm holds now key an
  IDENTITY BASIS (true pass-through at any parent pose) and the arc targets build on a parent-aware pass-through
  baseline, so a parent chain that moves during the deploy no longer displaces the tube; (c) empty tube match now
  fails loudly listing the animated part names; (d) dead `key_bone` removed. NOTE: the shipped `m114_deploy.glb` was
  generated by the OLD code and stays as-is (verified in-game); the fixes matter for the next artillery-style model.
- ~~**Feature Test Tier-2 bypasses `ConfigFor`**~~ — FIXED 2026-07-19: the animated fixtures now clone the registry
  entry and route through `ModelFactoryWindow.ConfigFor` like the smoke test, so `convertRig`/rotation/keep-flags all
  carry and the soldier is exercised on the conversion pipeline it actually ships on.
- ~~**Unify the delete-first suffix lists across bake paths**~~ — FIXED 2026-07-19: `SweepAllOutputs` (the full
  OutputSuffixes union, now incl. `_ClipsPoseData.bytes`) runs at the start of BOTH paths, so an animated↔static flip
  leaves no orphans in shipped Resources; the E5 rollback and the Feature-Test cleanup cover the pose bytes too, and
  the animated path gained the static path's up-front resource-name validation.
- ~~**District axis has no session re-arm**~~ — FIXED 2026-07-19: `RearmModelRegistration` now nulls `distFxManager`
  and every entry's `plbc`/`privateLeaf`/`leaves`/`collected`; `DistrictApplyEntries` re-derives them as the new
  session loads. Verify alongside the model-axis second-session test.
- ~~**Plugin perf pass (late-game GC stutter)**~~ — DONE 2026-07-19 (allocation-elimination scope, behavior
  untouched): precomputed `PoseNames`/`BoneRotationNames` (was `"Pose"+i` strings per pawn per frame); the pose hook's
  descId fallback is a plain loop (was a ctx-capturing lambda per pawn add); `ProcessFireQueues` prunes with a reverse
  for-loop (was a dur-capturing `RemoveAll` closure per entry per frame); `ProcessEngineAudio` throttles FIRST and
  caches its filtered subset keyed on the entries reference (was `Where().ToList()` 60×/s); `TickOne` hoists the
  field-name array and skips the 7 texture re-sets when `_MainTex` is already ours (re-set kept as the recovery path
  when the game recreates the material); the `[Grey] no _MainTex` retry warns once; the audio-trace postfix gained
  the try/catch every other patch body has. NOT done (deliberately): GetMember boxing elimination — it needs typed
  delegates over reflected structs, high risk for marginal gain; revisit only if profiling shows it matters.
  **VERIFIED in-game same day**: full animation sweep clean including the drone attack (fire-once path — exercises
  the queue prune, the descId-fallback loop, and the pose-name arrays in one action). Residual: informally watch a
  BIG late-game battle for stutter (the improvement claim, as opposed to the no-regression claim).
- ~~**Plugin unbounded per-instance dictionaries**~~ — FIXED 2026-07-19 (cross-session): all per-instance maps
  (`deployProgress`/`deployLastPos`/`customSources`/`loopHoldUntil`/`engineLastPos`/`engineMoving`, plus static
  `deployMoveState` and `respawnBase`/`respawnCount`) clear on session re-arm, and `deployLastPos` joined the
  in-session deploy prune. Remaining in-session growth of the engine-audio maps folds into the perf pass above.

## Quality-of-life / lower risk

- **RetextureWindow Apply-without-Edit** — MOSTLY FIXED 2026-07-19: Apply onto an existing entry the form wasn't
  loaded from now asks first (Edit pre-loads and skips the dialog). Still open: Apply can create a duplicate `Retex_`
  entry for a pawn that already has a model entry (two entries, same pawn, undefined winner).
- **TechTreeWindow** — PARTLY FIXED 2026-07-19: skipped edits now survive a partial save (only fully-written entries
  leave the overlay), and a MouseUp outside the canvas ends the drag (no more phantom teleport). Still open:
  `_pending` isn't serialized — a domain reload drops staged, unsaved edits.
- **ProjectileBaker** — MOSTLY FIXED 2026-07-19: sprite donors (null mesh) now refuse to bake with the verdict's
  guidance; Dump only auto-fills the donor field on a ✓ verdict; `ApplyTint` no longer wipes the clipboard. Still
  open: invalid impact-donor GUID silently ignored; muzzle swapped beyond the tooltip's documented scope.
- ~~**PropBaker**~~ — FIXED 2026-07-19: `FindType` is cached (the per-repaint full-AppDomain scan is gone) and null
  Amplitude GUIDs now fail the bake with the rebuild-then-re-bake guidance instead of writing zero-GUIDs.
- ~~**DatabaseBrowser**~~ — FIXED 2026-07-19: `ExitGUIException` is rethrown before the generic catch.
- **Animated multi-material albedos**: `LoadOrderedAlbedos` drops no-`map_Kd` materials (index shift → wrong rects) and
  can't load `.tga` (red placeholder) — the static path handles both.
- **Regex-fallback parser drift** (plugin): overrides-array objects parsed as models when `models` is empty; count
  truncation via min(pd,skel,atlas); early-entry key omission misaligns later entries; resourceName default differs.
- **Misc small:** registry Save wipes hand-edited pack wrapper metadata (matters when a second pack author exists);
  Lab bakes a brand-new never-baked entry with default model fields (Factory→Lab handoff carries only name/file/pawn);
  Browse's auto-set `animUnitFix` announcement is discarded by the ownership merge for existing entries; case-sensitive
  Upsert/Remove matching (case-only rename → twin entries); `atlasGuid` never validated; ~~`_ClipsPoseData.bytes`
  missing from the E5 rollback + Feature-Test cleanup lists~~ (fixed 07-19); ConversionGateTest litmus synthesis
  sequential `ReadToEnd` pipe-deadlock pattern; texture leaks on bake failure paths; corrupt-registry error-spam from
  per-OnGUI `Load()` in Retexture/Sound windows; ~~SoundWindow `ParseWav` negative chunk-size guard~~ (fixed 07-19);
  parity script false-PASS shapes (empty N/R sets; awk section extraction); no-op root collapse is
  dead code post-rebake (every bone gets keyed by the visual rebake); multi-armature sources mis-convert silently;
  `blend_export.py` repoints packed images it shouldn't; prep_model strip matches object names only (not mesh-data
  names, unlike deploy_convert); AtlasDebug likely double-converts in a Linear-color-space project; RefreshList comment
  contradicts the settled Factory-lists-all design; 3-strike registry give-up latches per-process ("this session" log
  text is wrong); Hk_AudioTrace postfix unguarded + per-event string scans; 4u fire-radius / 3u deploy-match adjacency.

## Queued for the package release (not review findings — branding/packaging debt)

- ~~**Framework identity migration**~~ — **EXECUTED 2026-07-19** (user call: zero external installs yet, so no compat
  period needed): assembly/DLL → `HumankindAssetFramework.dll` (csproj FILE name kept — local clones, build docs and
  the CLI compile-check unchanged), BepInEx GUID → `community.humankind.haf` (old cfg copied to the new name on this
  machine, old DLL removed from plugins in the same deploy — BepInEx would load both and double-patch), editor menu
  root → **`Tools ▸ HAF`** (all windows + Tech Tree + Database Browser consolidated under it; Tests submenu intact),
  instructional docs swept (Framework-Review's dated history rows keep their period-correct `Tools ▸ ENC` paths).
  **Deliberately NOT migrated** (framework/pack split, decided 07-14 and reaffirmed 07-19): `haf_models.json` /
  `haf_sounds` / `haf_skins` are ENC-the-PACK's files — packs are branded, only the framework is neutral, and a
  third-party pack never touches an `haf_*` path. **Verified in-game same day** (first session clean: new identity
  loads, settings carried, units/districts/audio normal). Still open for the package release: hardcoded paths,
  package scaffolding. (The `ENCAccessProof` C# namespace + project filename were renamed to `HumankindAssetFramework` on 2026-08-01; the local repo FOLDER followed on 2026-08-16 — nothing left of the old name.)
- **Pack pre-flight validator (third-party author DX)** — *legitimate gap, not yet built.* Today pack **structure**
  resolution is loud and human-readable (malformed JSON, duplicate `modId`, missing `dependsOn`, cycles, conflicts →
  clear warnings + `haf_load_report.txt`), and bad input fails *soft* (never crashes). But there's **no entry-level
  content validation**: a wrong bone name, an unresolvable GUID, or a missing texture path degrades silently rather
  than producing a "pack X, entry Y: bone `Z` not found" message. For a distributable framework this is a real
  barrier to entry for external authors. Build a **pre-flight linter** (editor button + a boot-time pass) that checks
  each entry's referenced assets/bones and reports mismatches in plain language before render. Fits the "guided, not
  guessy" design goal; scoped for the package phase. (Raised by an external review 2026-08-02; the structure half was
  already done in the 07-14/07-19 multi-mod work.) **Designed** — see
  [Pack-Validator-Design.md](notes/Pack-Validator-Design.md) (what to validate, editor vs boot-time surfaces, message format,
  phasing); build remains.

## Future feature seams (mapped, not built — the discovery is done, only the build remains)

- **TREADIZE v2 — hybrid link/shuttle rig (user's design, 2026-07-26).** On a straight run every link moves
  identically → ONE translating shuttle bone can carry the whole run (pattern maps at restart); per-link
  bones only on the WRAPS + RAMPS where links genuinely rotate. Bone math: Bradley ~23/track at full
  per-link wrap detail vs 75 today — quarter-link wrap smoothness inside half the budget. Skirted vehicles:
  the hidden top run can be fully STATIC (zero bones). The one risk is the two run↔wrap seams (static skin
  weights can't switch carriers) — mitigated by everything v1 learned: seams AT the tangent points, where a
  wrap link's velocity equals the run direction, speed-matched on the exact belt path. Prereq: none — build
  whenever tread bone budgets start pinching again (or for the twitch-ceiling escape).

- **Normal-map atlas support (shelved 2026-07-24; the one real UV-pipeline gap for the Ehrhardt's `Textures/` set).**
  Today the bake produces a SINGLE albedo atlas and the runtime injection NEUTRALIZES the donor's PBR (flat albedo). The
  **albedo half of a source set is already consumable** — bake the `BaseOp` down onto the game-mesh UVs — but the
  **`_Normal` maps are not processable at all**; that is the missing pipeline. To render surface detail the Factory would
  bake a matching **normal atlas** repacked to the combined-atlas UVs, and the injector would wire it into the pawn
  material's normal slot (`_BumpMap`) instead of clearing it.
  **What "fully process `Ehrhardt_E_V/Textures/`" actually takes (read off the shipped files, not hand-waved):**
  1. **UDIM assembly** — the chassis normal is **5 tiles** (`T_..._C_V*_Normal.1001–1005`) and the gun a single tile
     (`T_..._G_V1_Normal`); assemble the UDIM set into one image before repacking. (Same assembly the albedo/UDIM note
     below needs — build it once, feed both maps.)
  2. **Tangent-correct repack — the real work.** A normal map can't be atlas-packed like albedo: for every UV island the
     atlas rotates or flips, the normal's **R/G channels must be rotated/flipped to match**, or lighting inverts on those
     islands. This is why it's a pipeline *feature*, not just a second texture slot.
  3. **Normal-appropriate import** — linear (NOT sRGB) sampling, `TextureImporterType.NormalMap`, normal-safe
     compression + mips; a naively-imported normal atlas is read as colour and lights wrong.
  4. **Runtime wire-in, per variant** — point the pawn material's normal slot at our atlas instead of neutralizing it. The
     set ships **5 variants (V1–V5)**, each with its own `BaseOp` + `Normal`, so this composes with the
     runtime-retexture-variant axis (one skeleton/atlas, swap the pair per descriptor).
  Priority moderate: at map zoom (~80px units) the payoff is subtle — but this is the concrete build if/when we want it,
  and the Ehrhardt set is the ready test bed. Escape hatch today: bake the normal *into* the albedo's lighting in Blender
  (static, no runtime normal response) — cosmetic only. (If a source set also ships ORM/roughness/metallic, the same four
  steps extend to a packed ORM atlas + the material's metallic/smoothness slots.)
  **Related same-bucket gap — UDIM / multi-tile ALBEDO:** the bake assumes ONE texture per material in a single 0–1 UV
  tile, so the armored car's *cinematics* mesh + its 5-tile `.1001–.1005` UDIM camo can't be consumed directly. Escape
  hatch is the same manual Blender **texture-transfer bake** onto the single-tile game UVs. NOTE: a mesh authored with
  single-tile UVs (the armored car's *game* mesh) needs none of this — it bakes fine on the current flat-albedo path.

- **✅ Aim-layer REMAP — SHIPPED as "turretize" (2026-07-24), verified in-game on the Ehrhardt armored car.** Built as
  the `TurretizeAimLayer` runtime handler: `turretBone` (substring) + `turretAxis` (Lab dropdown) retarget the
  streamed heading slot onto our turret bone. Axis is per-model (Ehrhardt: 2 = yaw; 1/0 = pitch — the pitch axis is
  the future artillery-barrel elevation knob). Original design notes retained below for the static-model corollary.
- **Aim-layer REMAP — vanilla-style turret/head target tracking (requested 2026-07-24).** Vanilla units aim by a
  procedural bone-rotation layer: the sim streams the aim angle, the presentation writes it onto specific bones on
  top of the playing animation. **The layer still streams for our injected units** — but addressed to the DONOR's
  bone indices, which resolve to the invalid-index sentinel (0xFFFFFFFF) on our replaced skeleton and land on
  nothing (proven during the Law-5 fire investigation; the throttled `[Aim]` log in `ClearAimLayer` shows the
  stream). The feature is therefore an ADDRESS REWRITE, not an aiming system: an `aimBone` registry knob (name
  substring on our skeleton, the `handPropBone` pattern) + a remap mode where `ClearAimLayer` currently drops the
  entries — rewrite their bone index to ours, with an axis/offset knob (donor axis conventions won't match every
  model; stamp explicitly, the props import-angles lesson). Open: does elevation stream separately from traverse
  (second bone)?; does the sim only stream for donors it considers aim-capable (a donor-matching criterion)?
  **Intended first test candidate (2026-07-24):** an **Ehrhardt‑style armored car** ("Ehrhdrdt E V" by Red Blue Pixel
  Studio, Fab, Standard License, FBX + PBR) — it ships **already rigged with a turret bone**, so it's the EASY case
  (point `aimBone` at the existing turret bone; no auto-rig step). Bake static first, then remap the aim stream onto
  the turret bone once the feature lands.
  **The static-model corollary ("turretize"):** this gives STATIC models a tracking turret with zero animation
  authoring — split turret from hull (part-name detection exists), auto-create a 2-bone rig at the turret pivot
  and bind each part full-weight (the mech bone-parent→skin conversion's exact mechanics, just with created bones),
  bake through the animated path with a 2-frame identity clip (the held-stance pattern), then remap the aim stream
  onto the turret bone — the ENGINE animates the aiming, same as vanilla armor. Reactive motion (aim/facing) never
  needed clips even in vanilla; only cyclic motion (walks, bobs) does. Open extra: pivot placement quality
  (auto part-centroid vs a manual nudge knob).
- **Donor ground-FX suppression (`silenceDonorGroundFx`, spotted 2026-07-24).** Ground effects ride the DONOR like
  audio does: the Light Assault Mech (legged) stamps WHEELED TRACK decals from its APC donor. Fix = the donor-audio
  pattern, not a re-donor (animal donors are melee-presentation pawns — swapping would break the mech's ranged fight
  infrastructure): find the track/decal emitter chokepoint (likely MecanimEvent- or movement-state-driven FX on the
  sub-pawn — the same neighborhood the audio investigation mapped) and gate it per opted-in unit. Later composable
  with a "replace with footprints" mode. Adds GROUND FX to the donor-matching criteria list (rotor/wheels, audio,
  ranged capability, aim streaming, now decals).
- **Muzzle-flash relocate — ✅ VERIFIED IN-GAME 2026-07-24** (commit `1751b74` "muzzle endgame lands — flash, smoke and tracers on the tracking turret"; was its own scoped session).
  Implemented as the `muzzleBone` field + `Hk_MuzzleRelocate` prefix on `PresentationSubPawn.GetBoneTRS(string)` — see the
  cracked mechanism + fix below; ArmouredCar set to `muzzleBone: "Turret"`. The flash now anchors on the turret/gun on
  fire. (If a turret pivot ever reads too low/centre on another model, pick a barrel-tip bone instead.)
  On the Ehrhardt armored car the MG muzzle flash fires off-side ("mirrored"). ROOT CAUSE (verified): the donor is
  `Unit_Era6_Common_AntiAirGuns_01` (an anti-air gun — bones `Azimuth`, `bras-*`, `Canon_down_*`), and the flash is
  the projectile's **`Muzzle` FxEvolverMaterial** ("launch flash", `ProjectileAsset.muzzle`) — a TRANSIENT VFX (NOT a
  fragment; every donor lists only its body mesh) spawned at the AA gun's `Canon` weapon socket, which doesn't exist
  on our renamed `b###_` rig → it lands off-side. The spawn is NOT in `PawnRangedFightSequence` (stores the shooter
  only) nor `PresentationPawn` (3525 lines, no muzzle/socket) — it's buried in the **HgFx projectile/particle
  system**. CHAIN TRACED (2026-07-24): the projectile+muzzle fire from a **FireProjectile mecanim event** on the
  attack clip -- `PresentationSubPawn` scans the clip for `MecanimEvent.AlterationType.FireProjectile` and stores it
  as `SimpleAttackMecanimEvent` (~L1255-1267), processed by `MecanimEventInterpreter` (`Amplitude.Mercury.Animation`)
  as the clip plays. The bone->world resolver is **`PresentationSubPawn.GetBoneTRS(boneName)`** (~L378:
  `GetBoneIndex(boneName)` -> `AnimationManager.GetBoneTRS`). The AIM layer resolves the SAME way (SubPawn ~L639/657:
  `GetBoneIndex(reference.BoneName)` -- the donor's `Azimuth`/`Canon` names), so the muzzle socket almost certainly
  resolves by the donor's weapon-bone NAME -> invalid on our `b###_` rig -> off-side. Fire info via
  `IAlterationFireProjectileInfoProvider` (SubPawn L179/813 = the pawn). NEXT: decompile `MecanimEventInterpreter`'s
  FireProjectile handling (NESTED-type friction with ilspycmd 8.2 -> use dnSpy or a newer ilspycmd) to pin the
  socket-NAME source + the muzzle-FX spawn call. QUICK-ALT CAVEAT: the `ProjectileAsset` is SHARED across all AA guns,
  so nulling its `Muzzle` in place breaks the real anti-air units -> needs a per-unit projectile OVERRIDE.
  ✅ **MECHANISM FULLY CRACKED (2026-07-24, decompiled Assembly-CSharp whole).** `AlterationFireProjectile.StartEvent`
  (the FireProjectile alteration handler): `TRS boneTRS = controller.SubPawn.GetBoneTRS(mecanimEvent.ParentNameToLaunchVFXPosition);
  Vector3 startPosition = boneTRS.Transform(mecanimEvent.PositionToLaunchVFX);` then
  `PresentationProjectileManager.Instance.LaunchMuzzle(projectileAsset, startPosition, startDirection, up)` (or
  `LaunchProjectile` for the flying shot). So the muzzle position = **`SubPawn.GetBoneTRS(<donor socket name>).Transform(offset)`**
  — and `ParentNameToLaunchVFXPosition` is the DONOR clip's socket name (the AA gun's Canon socket), absent on our
  renamed rig. **THE FIX (low risk):** Harmony **postfix on `PresentationSubPawn.GetBoneTRS(string boneName)`** — for
  our unit (match SubPawn→entry by SkeletonId, `GetEntryBySkeletonId` exists) with a `muzzleBone` set, when
  `Skeleton.GetBoneIndex(boneName) < 0` (donor socket not on our rig), replace `__result` with `GetBoneTRS(ourMuzzleBone)`
  (our bone IS found → no re-redirect → recursion terminates). Config = `muzzleBone` (substring, e.g. `Turret` or a
  central bone), runtime-only. Broadness note: this redirects ALL unfound-socket VFX on our unit to `muzzleBone`,
  which for a donor-mismatched rig is the DESIRED behavior (all its VFX land on our gun instead of off-side). QUICK ALT (no relocate): null the
  projectile's `Muzzle` → no launch flash (Projectiles.md already documents clearing it). Note: this donor is one of
  the few that fire MULTIPLE times (AA burst) so the flash repeats. General lesson recorded: **a donor's effect = its
  skeleton + weapon sockets** (already half-logged: `donor.Skeleton` / `BoneInfos` / `donor fragment[N]`). The new
  **Disable override** flag (`ModelDef.disabled`, runtime) A/B's our model vs the raw donor for exactly this kind of probe.
- **DONOR SOCKETS (`socketBones`) — ✅ VERIFIED IN-GAME 2026-07-24 night (ArmouredCar): flash, smoke AND tracers
  all on the tracking turret.** The winning recipe: `socketBones: "Canon_Up_left=MW_T;Move_bloc=MW_T"` (socket
  ROLES decoded from the pin log: `Move_bloc` = fire POSITION anchor, `Canon_Up_left` = rotation/direction — not
  what the names suggest) + runtime donor-offset compensation on native socket hits + the **`muzzleOffset` world
  dial** (`"0,2.6,0"` — the rig's gun-bone head sits at the model base, and the socket's correct BIND height
  provably does not reach the runtime pose; open engine question, the dial closes it empirically, no re-bake per
  step). War-story hazards now guarded: prefix reentrancy (stack-overflow crash), the external-registry-edit slim
  cache trap, per-shot log throttled to once-per-entry after calibration. Wired
  end-to-end: rig_anim argv[11] (exact-named zero-weight leaf bones after the rename, before the fold; `A###_`
  prefix on socketed models; loud failures for unmatched parents and sort-order violations), BakeConfig/ConfigFor/
  slim-cache diff, Lab "Donor sockets (bake)" field, ModelDef.socketBones (bake-time; guard PASS). The ArmouredCar
  entry is pre-configured (`Canon_Up_left=MW_T; Move_bloc=Root`) — next session: Unity recompile → re-Bake →
  rebuild → fire: flash, smoke AND tracer origin should all sit on the (tracking) turret gun natively. Original
  design rationale below. The interception chain
  (GetBoneTRS redirect → StartVFXEvent pin → offset compensation) moved/killed the FLASH but smoke + tracer origin
  still read the donor socket, and the compensated TRS raised a space question (flash vanished off-screen). The
  correct architecture: bake EXACT-NAMED donor socket bones onto our rig (`socketBones: "Canon_Up_left=MW_T;..."`,
  zero-weight leaves, optional tip offset) so the game's own lookups resolve NATIVELY — flash, smoke, and bullet
  origin all correct-by-construction and turret-following. Wrinkle: Amplitude sorts bones alphabetically requiring
  parents-first — socketed models switch the rename prefix `b###_`→`A###_` so every real bone precedes any donor
  name (gated; existing bakes byte-identical). Obsoletes muzzleBone for rebaked models; the runtime knobs stay for
  quick fixes. Donor socket names discovered via the [Muzzle] GetBoneTRS diagnostic (armoured car donor asks for
  `Canon_Up_left` + `Move_bloc`).
- **TRANSLATION UNLOCK — SHIPPED & VERIFIED (2026-07-25/26).** The engine plays `RotationTranslation` clips
  (decompiled: vanilla tank treads/shuttle bones; `GetPoseTRS` zeroes translation only for Rotation-encoded
  curves) — Laws 1/5 were OUR bake's strip. Built: per-model `keepTranslations` (registry + Lab toggle), kept
  curves scoped to the attack clip, delta-rebased, ×100 sandwich-compensated on the legacy path; multi-segment
  recoil windows with `/N` speed steps. Verified end-to-end twice: a sliding test bone, then **the M114's real
  kickback** (recipe `442..530,305..441/2`, Return 0, Slam 0). Root-caused en route: the slam-0 R=1e9 sentinel
  put the RecoilArm pivot at a billion units → float32 chain collapse → every historical NaN import warning.
  OPENS: **treadize** (tank tread shuttle bones — design ready, Jagdpanzer waiting), real deploy translations,
  whole-carriage recoil, soldier run-bob restoration.
- **Fire-effect refinements on the verified muzzle system (spotted 2026-07-24, unbuilt).** The pin log showed the
  AA-gun donor's multiple barrels as VARYING per-event offsets (`donorOff=` 0.80/0.85/1.20) from the single
  `Move_bloc` anchor; the compensation currently flattens all onto one point. (1) **Barrel variation** — subtract
  the MEAN donor offset instead of each event's own: flashes scatter slightly around the muzzle like the donor's
  real barrels, essentially free. (2) **Multi-mount fire** — rotate successive fire events across several of the
  model's own gun bones (the Ehrhardt has four rigged MG mounts, `MW_B/F/L/T`) — needs per-event socket selection
  state; bigger. Both are polish on a verified base, not fixes.
- **"Vehicleize" — VERIFIED IN-GAME 2026-07-25: the shipped ArmouredCar now runs a Lab-generated rig** (grounded,
  turret aiming, muzzle flash calibrated). The first real-model run (3,350-shard Ehrhardt rip) drove a day of
  hardening, all field-verified: per-hub wheel **clustering** (per-part bones shred wheels — off-axis spokes
  pinwheel about their own bbox centers), per-bone **join** (3,350 objects timed out the bake's 180 s Blender
  step; 6 meshes take ~11 s), **stowaway-skeleton strip** (`SKM_` rips carry their own armature — two skeletons
  in one GLB), `@file` part lists (the ~32 k Windows command-line limit), Blender 5.x `Action.fcurves` removal
  (curves live in `layers→strips→channelbags`), spin-sign rule (+360 = forward for a +X nose), review UI
  (6 roles incl. Edgecase, keyboard marking, classification filter, 4 hide sliders), JSON recipes, and a
  clustering-accurate **Verify** report. Generated-rig calibration: turret axis **Y**, sockets/muzzle bone →
  `Turret`, offset re-dialed from the dome center. **SKM fast path — BUILT same day, preview-verified:**
  probe detects skeleton + ≥90% weights → bone-marking mode → `rigfast` spins the SOURCE bones (local axle axis,
  signed for mirrored rigs), artist skeleton shipped unchanged (pivots + `MW_*` socket bones free). Field
  finding: it inherits the artist's weighting — the Ehrhardt's front steering knuckles are weighted to the wheel
  bones and rotate with them, so the shard path stays the quality reference (the shipped unit uses it); the fast
  path is the four-checkbox route for clean-weighted rips. Original spec below. The Ehrhardt's
  `_Spin.glb` was hand-made in Blender (now documented step-by-step in Animated-Models.md); the tool version is the
  missing sibling of turretize and the biggest lever on the "huge pool of static vehicle models" thesis: a headless
  Blender script that (1) detects wheel parts — name pattern `wheel|tyre|tire` first, geometric fallback (cylindrical,
  near-ground, mirrored pairs — the organ-gun classifier's approach), (2) creates Root + a bone per wheel at each
  part's centroid (+ a Turret bone for a `turret`-named part), rigid full-weight skinning, (3) generates the LINEAR
  `Spin` action (frame 0 = rest), (4) exports `<name>_Spin.glb`. Factory affordance: a "Prepare static vehicle…"
  button that runs it and repoints the Model file. Output feeds the EXISTING verified path (Spin[0..0] idle +
  Spin slice movement + convertRig + auto-ground + turretize/sockets). Risks: wheel detection on messy meshes
  (single-mesh models need loose-part separation), axle-axis inference (mirrored left/right wheels spin opposite
  if the axis flips — normalize to model-space).
- **Death clip role (`clipDeath`)** — play the model's own death animation on `PresentationPawn.TriggerDeath` (the
  hook already fires for the death SOUND; arming a one-shot clip window from the same seam is the pattern the
  attack clip proved). Proving model: the gray wolf's `idle injured to dead reaction lft/rgt` (private test rig).
- **Idle perimeter patrol** — presentation-only stroll around the tile while plain-idle (`idlePatrolRadius`/
  `idlePatrolSpeed`): offset ObjectSpace.Translation along a slow closed loop (the position-offset path already
  writes Translation per frame), play the MOVE clip, face the path tangent (needs an ObjectSpace.Rotation write —
  read-only today). Risks: stride matching (path speed vs walk-clip foot speed, or it ice-skates), yielding to
  every real state, battle second-PresentationUnit interactions. Composes with idle-alt: stroll → pause →
  howl/eat → stroll.

## Verified clean (don't re-litigate without new evidence)

GUID nibble-swap encoding + keep-GUID re-bake; registry corrupt-guard/atomic-write/backup lifecycle; two-window
ownership merge (both directions, post-fix); Harmony patch exception discipline; cross-thread sample locking +
ConcurrentQueue handoff; deploy ramp math; join/decimate + albedo-extraction blocks; frame clamping; noise-filter
re-entrancy; district bake+registry editor flow; Plugin.cs config wiring.

---

# 2026-07-31 audit — "silently disarmed" class

Ten open findings from the pass that followed the Abomination spike-geometry incident (root cause: a safety net
that could never arm itself). Recorded separately with file:line, in-game symptom, trigger and suggested fix:
**[Audit-2026-07-31.md](notes/Audit-2026-07-31.md)**.

Top item (now FIXED in `c6154a6`, pending in-game verification) — the wrong-skeleton rescue was gated on `Hooked` (animated-or-freeze), so eight shipped STATIC models have
no rescue path at all: the same failure `0c0b12f` fixed, still live for them.

# 2026-08-16 critical review — whole framework (plugin + editor)

A full multi-agent review of the plugin and editor. **All CONFIRMED findings were fixed, verified in-game/at-bake,
and merged** — district clone leak, `hideSubPawns` coexistence, `coreDesc` matcher unification, formation
pure-repoint reform, GameBinding army-walk-root coverage, audio death/battle gate, three runtime-clone leaks,
state-machine gate mismatch, the facing-after-respawn interaction, and the three bake silent-mis-bake guards (4A/2A/4B).
See the dated CHANGELOG entries. What remains below is the **PLAUSIBLE / low-confidence tail** — deferred, not
dismissed.

## Bake scripts — PLAUSIBLE (ENCReload `Tools/`, needs a failing repro before touching gating)

- **1A — `convert_rig` vs `clean_units` gating asymmetry** (`rig_anim.py`: topological bone rename gated on
  `convert_rig` alone, but the clean-unit export + rest/scale fold on `convert_rig OR clean_units_input`). A
  `DeployArmV2` FBX with argv[8] absent/`0` + zero rotation → `convert_rig=False`, `clean_units_input=True` → clean
  export runs but bones keep raw part names → Amplitude's alphabetical sort can put a child before its parent →
  ParentIndex ≥ own → model explodes. **Fix candidate:** make the two gates the same flag. **RISK:** changing bake
  gating without a failing repro can break a verified path — get a repro first.
- **4C — empty/constant primary action → frozen clip with exit 0** (`rig_anim.py` ~510-520): the `kept == 0`
  hard-fail only runs when a bone-prefix filter is supplied; a no-prefix bake of a constant action bakes frozen.
- **1B — ordinal vs culture sort** (`rig_anim.py` ~1038): the socket-order guard uses Python ordinal `>`, but it is
  predicting C# `string.Compare` (culture-sensitive) — a donor name whose culture order differs from ordinal order
  passes the guard yet sorts the socket before its parent. Narrow (uppercase donors agree).
- **1C — `%03d` bone-index width** (`rig_anim.py` ~998): `A1000_` sorts before `A999_`, inverting order above 999
  bones. Unreachable under the 240-bone cap today; a hard assumption worth a comment.

## Plugin — low-risk hardening (Tier 4)

- **Harmony `TargetMethod` param-count filters**: `Hk_DistrictGroundMaterial` / `Hk_DistrictHexSculpt`
  (`UniversalInject.Hooks.cs`) resolve by method name with no `GetParameters().Length` filter (unlike their
  siblings) — a future overload could be patched silently. `Hk_BattleTurnProbe` (`BattleTurnPatch.cs`) indexes
  `GetParameters()[0]` without a length check.
- **`Hk_SilenceEvents.Prefix`** (`Hooks.cs`) reads `eo.name` (native marshal alloc) on every Wwise `PostEvent`
  before its gate; mirror `Hk_AudioTrace`'s early-out. (Both also patch the same `PostEvent` = two detours/sound.)
- **Belt-and-braces `try/catch`** on the multi-call postfix bodies of `UniRegisterHook` / `UniRepointHook` /
  `Hk_DistrictRepoint` — they sit inside core loading methods and rely entirely on every callee being self-guarded.
- **`LongestMatch` equal-length tiebreak** (`UniversalInjectPatch.cs` ~889): among equal-length key matches the
  first in registry order wins; the `count>1` warning fires but the (possibly wrong) bind still proceeds.
- **`TryLearnClass`** (`UniversalInject.Clips.cs` ~198): takes the FIRST class-sample within 2u (not nearest) and
  caches it permanently — a stacked neighbour of a different class can mis-categorise a unit's turn-ease for the session.
- **Diagnostic-map growth**: `deployMoveState` (`Combat.cs`) is nulled cross-session but never pruned within a
  session (siblings are).
- **Muzzle-compensation stash** (`Combat.cs` ~605-624): module statics assume `StartEvent→GetBoneTRS→EndEvent` is
  atomic; nested/interleaved fires of coexisting shooters could cross offsets. Confidence limited (needs the engine
  to actually nest these).
- **`BoneRotation` slot clobber** (`UniversalInject.Pose.cs`): on the `useDonorClip` path `ApplyRotorSpin` /
  `ApplyRotorTrim` / `ApplyGunElevation` write overlapping low slots — a rotor-spin + trim combo can clobber.

## Data contract — low (editor ⇄ plugin)

- **`rotorSpinBones` / `rotorSpinSpeed`** are plugin-only fields with no editor `ModelDef` field. *Partly addressed
  2026-08-16:* the schema-parity guard now **allowlists** them as intentional runtime-only keys (like `scale`), so the
  gate is green — but the underlying risk stands: a hand-authored pack.json value is silently wiped on the next Factory
  Save (JsonUtility serializes only `ModelDef`'s fields → unknown keys dropped). Same wipe hits every allowlisted
  runtime-only key. The real fix is a round-trip that preserves unknown keys (or promoting these to `ModelDef`); latent
  (ENC unused), so deferred.
- ~~**`idleAltInterval` default mismatch** (editor 25f / plugin 0f) — a pack.json missing the key gets idle-alt
  disabled instead of the documented 25s cadence.~~ **FIXED 2026-08-16** by the shared-schema field initializers
  (one authoritative default = 25f, test-pinned; see the Framework-Review generic-parse row).
- **`haf_districts.json` has no regex fallback** — one malformed char disables ALL custom districts (the model
  registry has a fallback; districts don't).
- **`animated` flag written but not read** — the plugin infers animation from the clip-GUID presence, so the field
  is a silently-ignored authored value.
- Regex-fallback float fields can't parse exponent notation (`1E-05`); narrow (malformed-JSON path only).

## GameBinding — remaining catalog gaps (extend the "make drift loud" template)

**Progress (2026-08-16, reflection-fragility A5):** the catalog now also writes a machine-readable
`haf_bindings_report.txt` every launch, the last raw `Type.GetType` site was migrated onto an accessor, and a
member audit took coverage from 31 types / ~49 members to **49 types / ~124 members** (verified `missing_members=0`).
See CHANGELOG + Framework-Review A5. What remains:

- **District-ground/hex support types resolved reflectively but off-catalog:** `AssetReferenceRepository`,
  `Amplitude.StaticString`, `GroundMaterialDefinition`, `AnimationVariableNames`, `HgFxAnchorComponent`. A rename
  there degrades silently. (The `SimulationEvent_*` combat types resolve with their own local warnings, so they're
  loud-but-off-catalog.)
- **Members read off types not yet in `GameBinding` at all** — the `Skeleton`, pawn-entry / `GPUPawnDescriptorEntry` /
  fragment **structs**, `FxOneMeshStruct`, `PresentationLevelBuildComponent` on the hottest injection path (the
  member audit surfaced these; structs need new accessors + a different resolution, so it's a distinct batch).

## 2026-08-17 verified critical review — surviving deferrals

A full-framework review, adversarially verified finding-by-finding against the code and this project's own record
(see the Framework-Review 08-17 row; the fixed-same-day HIGH — the glbconv source split-brain — is in the
CHANGELOG). Most findings were already admitted here or ADR-settled. What survived as **new and deliberately
deferred**:

- ~~**Generic parse binds runtime-state fields from pack JSON** — `ModelEntry`'s public `repointed` / `descId` /
  `animId` bind from any name-matching key (the old hand-list parse was an implicit whitelist), and a key colliding
  with a readonly collection (`phaseTracks`) throws inside `ToObject` → the whole pack silently drops to the regex
  fallback. The parse-site comment assumes no matching keys exist; nothing guards it.~~ **FIXED 2026-08-17** with a
  config-key **whitelist strip** before the generic map (`registryConfigKeys`: shared-schema fields by reflection +
  the GUID arrays + plugin-only config) — fail-safe for new runtime-state fields, chosen over per-field
  `[JsonIgnore]` (fail-open: one forgotten attribute reopens the hole). Two pinning tests (hostile state keys →
  defaults; readonly-collection collision → stays on the object parse). Suite 61 → **63**.
- ~~**No CI service** — the entire automated gate is the per-clone opt-in pre-push hook on one machine; the docs say
  "CI-able" three times but nothing runs the build/tests/bindcheck automatically. A GitHub Actions workflow closes
  it (the gitignored `References\` DLLs need a strategy first).~~ **FIXED 2026-08-17**: `.github/workflows/ci.yml`
  builds + runs all 61 tests on every push, using `tools/fetch-refs.ps1` (reference DLLs from public sources — the
  vestigial Amplitude reference turned out removable, so no game files are needed). bindcheck stays manual (needs
  the game's DLLs).
- **No offsite copy of the unversioned working set** — all code is on public GitHub, but the licensed source models
  and baked assets exist only on this machine plus same-machine `D:\HAF_Backups` (now noted in Backup.md). One disk
  event loses the un-reproducible half of the project.
- **`cb` vs `cbb` GUID-component naming** (clip vs combat-clip; also `ca`/`cba`/`aca`/`a2a`) — a one-character typo
  in the 44-int wiring compiles clean and mis-wires a clip role; nothing tests the field→`InjectClipCollections`
  wiring. Rename or add a wiring test when next touching the schema.
- ~~**Ghost-hunt log tags bypass the quiet-by-default `Diag` gate** (`[REND]`/`[SRCFIX]`/`[CRUSH]`/`[GHOST]`/`[DESC]`,
  added 08-03/04 after the Phase-3 quiet-logging pass; most are change-gated or one-shot, but the `[REND]` census can
  log ~26 `LogInfo` lines / 15 s per `hideSubPawns` entry).~~ **FIXED 2026-08-17**: the 15 automatic lines (incl.
  `[HIER]`/`[LAYER]`/`[FX]`) now go through `Plugin.Diag`; the operator-driven `[BISECT]`/`[REND2]` command responses
  deliberately stay loud (they answer a typed `haf_ghostbisect.txt` command).
- ~~**Stale comment** — `Plugin.cs:83` still names `community.humankind.encaccessproof.cfg`; the live config is
  `community.humankind.haf.cfg`.~~ **FIXED 2026-08-17.**
