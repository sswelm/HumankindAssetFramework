# HAF — Milestones & project history

A reverse-chronological-ish log of capabilities as they were first proven in-game, with the war stories
behind them. This is the project's memory: what was hard, how it was cracked, and when. For *what HAF does
today*, see the [README](README.md) and the [docs index](docs/README.md); this page is the trail that got us here.

Dates are first-verified-in-game. Many entries pre-date the dating convention and carry no date.

---

## Infrastructure

- **DISTRICTS + FORMATIONS INHERIT THE COLLAPSE (2026-08-20).** Units got the ONE-file registry on 08-19; the
  district and formation registries still ran the old two-file pattern with none of its protection. Rather than
  two more hand-copies, the machinery moved into a shared `SingleSourceRegistry<TFile>` engine — git-tracked
  source, deployed build artifact regenerated on every Save, one-time migration, artifact recreation + drift
  warning, pinpointed corruption (line/column), timestamped preservation, once-only logging, Save lock, and
  one-click recovery from the last deploy or the last commit — and `DistrictRegistry` / `FormationRegistry`
  became thin typed shells (188→137, 181→133 lines; public API unchanged). The District Factory and Formation
  Override windows carry the Factory's red recovery banner. Two rules the shared engine adds over the first
  cut: migration **never overwrites a newer source with an older deploy** (the loser is preserved beside the
  artifact), and content comparisons are **CRLF-normalized** — found necessary on the spot: the live district
  source and deploy differed by exactly 143 carriage returns and nothing else, which would have fired a false
  "hand-edited" warning on the first load. Source files keep their historical `.backup.json` names to spare
  git a rename; `SourcePath` is the honest accessor. Backlog #3 follow-up closed. **DRILLED the same evening**: a
  comma deleted from the district source → banner pinpointing *line 22, position 16* (`districts[0].rotation.y`),
  one Console error, the corrupt copy preserved timestamped → **Restore last deploy** recovered 2 entries eleven
  seconds after the break.

- **PROBE PARTS 116 s → 7 s (2026-08-20, "can you optimize it?").** The Ehrhardt probe had crept from half a
  minute to two. Per-phase timers (now permanent `VEHICLE timing:` lines) convicted two phases, neither the
  split nor the import: the escape-ray **visibility** pass (31 s — `scene.ray_cast` walked all 3,350 objects per
  ray) and the **preview export** (86 s — the FBX exporter writing 3,350 *skinned* objects, 58 MB). Fixes: one
  `BVHTree.FromPolygons` over the world-space scene (0.3 s, same verdicts ±3 parts at the eps edge) and an
  unskinned meshes-only preview (2.1 s, 11 MB). Because the bone-row highlight had just been built on the
  preview's skin weights, the probe now emits each shard's dominant bone as a 7th `PART` field and the Lab
  maps bone → shards from the part list — which also made visible what the user had been hunting: the Turret
  bone owns **567 shards**. Plus a sentinel + flush after the PART lines, because Blender's late-flushed
  version banner glued itself onto the last row during verification.

- **VEHICLE-LAB CLOSERS (2026-08-20).** The two loose ends from the canoe forensics, closed in one pass.
  (1) *The recipe-predates honesty note*: loading a recipe now names the features it predates ("recipe
  predates: wave rock, spin switch — loaded as safe defaults; Save to modernize"), detected by key-presence
  in the raw JSON since JsonUtility can't tell absent from default — the invisible fallback that cost GLB
  forensics to diagnose now announces itself (7 of the 9 recipes on disk trigger it today). (2) *The
  hand-list gate grew a third block*: every `Recipe` DTO field must be written by SaveRecipe AND restored by
  LoadRecipeFromPath, or the push fails naming the field — drilled by planting a field, caught on both sides
  (23 fields round-trip green). The canoe-style silent field loss is now structurally impossible, the same
  treatment the Factory/Lab ownership lists got on 08-19. Same evening: the **Edit existing** dropdown shows
  each recipe's last-modified stamp — a bare name list can't tell you which one you worked on yesterday. And a
  drill-by-use catch: "the Ehrhardt stopped highlighting" — it never had: a rigged SKM source defaults to the
  fast path whose rows are BONES, and the highlighter matched renderers by name. Bone rows now tint every shard
  whose skin weights point at that bone (tallied once per preview); shard rows match exact-before-prefix.

- **THE BAKE TESTS WINDOW (2026-08-20, "this looks ridiculous").** The test pyramid had grown one guard at a
  time into seven bare menu items — "Bake Conversion Gate Test (litmus)"? — each talking to its own dialog,
  with no way to tell what a test did without reading source. The user called it: *"we need a specialized
  testing dialog with clear explanation what we are testing… the center testing suite with clear UI
  feedback."* All seven items collapsed into **one window** (`Tools ▸ HAF ▸ Bake Tests…`): every test is a row
  with a plain-language what-it-tests and what-it-costs, Quick/Everything presets, one Run button, LIVE
  per-row PASS/FAIL (a delayCall queue runs one test per editor tick so rows turn green/red as they finish;
  failures unfold their detail lines), and a durable `Logs/haf_bake_tests_report.txt` per run — the editor
  twin of the runtime's `haf_smoke_report.txt`. The tests themselves (`BakeSmokeTest` / `BakeFeatureTest` /
  `ConversionGateTest`) now return a `BakeTestSection` instead of popping dialogs — and SKIPs are counted
  honestly instead of being smuggled into the pass count. Same day, the suite's first ALL-models run earned
  its keep in reverse: 6 "failures" that were actually a **stale assertion** — the smoke test's 1 KB
  pose-stream floor predated the one-frame `Spin[0..0]` idle pattern, whose real shipped pose streams run
  48–960 bytes. The test would have failed the live, in-game-working assets; the boundary (every failure a
  one-frame idle, every pass a multi-frame clip) convicted the test, not the bakes. Floor recalibrated to 32
  bytes (smallest legitimate asset: 48) with the conviction recorded in a comment. First-sight drill catch by
  the user: the two smoke scopes could both be checked (baking every model twice) — they're now mutually
  exclusive radio rows, and the Everything preset picks the thorough one. The user then drove three more UX
  rounds: row titles became the plain question each test answers ("Is rig conversion still correct?" — the
  jargon 'litmus'/'smoke'/'golden' demoted to the descriptions), and a two-round layout fight (titles clipped
  to "…(c", then cramped into a ~180px column) unearthed a real Unity gotcha — `ToggleLeft` with a
  word-wrapping style mis-sizes itself; the fix is a checkbox + label-skinned button that takes the full row
  width. **DRILLED 2026-08-20**: the full Everything run — all seven rows, smoke-ALL through deploy goldens —
  came back 54 passed, 0 failed, 1 skipped (the texture-only corvette) in 6.3 min, one report file.

- **THE SIZE-REFERENCE KIT (2026-08-19, user-designed piece by piece).** "A default humankind man as a
  reference would really help assess size" → both previews (Factory + Lab) gained a **Ref man** — a stylized
  figure at game human height with X/Y position dials — and a **Ruler** (vertical stick, 0.5u ticks, long
  ticks at whole units; units not meters, since every bake picks its own world scale). His height was
  **calibrated 0.9 → 1.1 → 1.85** the waterline way and verified head-to-head against a human-scale soldier
  model. The build was its own three-round drill: hand-rolled winding culled every face ("I don't see any
  man"), shared-vertex double-siding zeroed the normals, and the final form — flat-shaded triangles in both
  windings via one shared box-prop builder — is now the house pattern for preview props. Rendering hand-built
  meshes joins the lesson list: emit both windings with per-face vertices, always. **Art-direction rounds
  (same evening, user-driven)**: classical figure proportions (head ~1/7, legs half with a gap, arms to
  mid-thigh), slimmed depth, and a sphere head — itself converged by bisection (0.072 too big, 0.055 too
  small, 0.063 landed) at 14×20 tessellation so flat shading still reads round. Every dimension is now a
  named parameter; further taste changes are one-liners.

- **VEHICLE LAB: RECIPE FORENSICS + THE SPIN MASTER SWITCH (2026-08-19).** The canoe's recipe "lost" its wave
  configuration — actually never had it: the recipe predates the wave fields, and absent JSON fields load as
  C# defaults (the honest choice; inferring "wave on" from the `_Wave.glb` filename would be guessing). The
  values were **recovered by forensics on the shipped GLB** — decoding the 361-key quaternion track gave pitch
  2.4° × 1 swing, roll ≈ 0, over a **15-frame** cycle — which also caught the restore's own first error: the
  modern default of 120 rock-frames would have slowed the shipped bob 8× (at generation time the rock-frames
  argument didn't exist and fell back to Spin frames = 15; the rig script's clip length is
  `max(spin frames, rock frames)`, the one real spin↔wave coupling, now named in the UI instead of hidden).
  Then two UX rounds on the Spin section: it **grays out as "inert"** when no wheel/rotor/turret is marked
  (with the clip-length exception disclosed), and — the user's follow-up exposing the gating's blind spot —
  an **"Enable spin animation" master switch**: disabling spin on a *wheeled* vehicle used to mean unmarking
  every wheel (the wave-checkbox lesson of 08-01, relearned). Off = zero spin degrees + forced-static tracks
  at Generate; bones, markings and dials all survive toggling; the recipe field defaults TRUE so old recipes
  keep their motion. Recipe save/load hand-lists updated on both sides.

- **THE LOGGING AUDIT — and the two real holes its questions exposed (2026-08-19).** Asked "how good is our
  logging?", the survey said: 707 plugin log calls, 264 editor calls, 10 machine-readable files, 12 one-shot
  guards — runtime logging excellent and battle-proven, with three gaps, all filled: (1) **invariant
  formatting** as infrastructure + policy (`Plugin.Inv`; a wrapper can't retro-fix current-culture
  interpolation, so the combatZ line is the live exemplar; config parses audited culture-safe already); (2)
  **`Plugin.Once(key)`** — a keyed one-shot gate so log-once stops being hand-rolled statics (15 legacy guards
  stay — several are load-bearing state; the dead one deleted, making the build warning-clean); (3) a
  **durable editor action log** — every HAF-prefixed Console line appended timestamped to
  `Logs/haf_editor_actions.log` (5 MB rotation), because Editor.log is per-session and unstamped. Then two
  follow-up questions found real holes: **"are the logs backed up?"** exposed that the backup config group's
  `*.json` glob silently missed every hand-tuned runtime file (hug/turn/battle/rotor tuning, the plugin .cfg,
  ground-tex/state dirs — not regenerable, not in git, in NO backup; now included), and the fix trail exposed
  that the **compile gate's hand-listed sources had drifted** — three editor files (GameSoundLab, HafCli,
  SoundOverrideRegistry) were NEVER compile-checked; sources are now discovered at run time, retiring that
  hand-list for good. Questions are drills too.

- **CORRUPT-SOURCE PINPOINT + ONE-CLICK RECOVERY (2026-08-19, user design: "not only a try/catch but recovery
  functionality") — DRILLED.** A hand-edit that breaks pack.json now gets: a **pinpointed error** (Newtonsoft
  re-parse purely for diagnosis — the drill's planted missing comma reported as "line 19, position 12, path
  models[0].scale"); a **timestamped preserved copy** (a second corruption never overwrites the first's
  evidence); and a **red recovery banner** in the Factory with one-click paths, each validated (must parse and
  hold models) before writing: *Restore last deploy* (the artifact — freshest valid copy, no git needed),
  *Restore last commit* (git checkout, then validated like any candidate), *Open broken file* (fix the named
  line by hand). Save/Bake stay locked until recovered — the no-wipe guarantee unchanged. Drill: planted comma
  → banner named the exact line → Restore-last-deploy brought all 22 models back with the corrupt copy
  preserved. Drill finding fixed same hour: the corrupt error logged on EVERY Load poll (dozens of Console
  lines for one broken file) — now once per corruption; the banner is the persistent surface. Also from this
  exchange, a process lesson recorded: the editor already logs its actions — the drills' narration burden was
  the operator not reading Editor.log.

- **THE PACK.JSON COLLAPSE (2026-08-19) — one registry, one truth (backlog #3 closed).** The deployed/project
  pair — deployed authoritative, project a dual-written shadow — surprised every external tool and fed the
  coherence-drill era. Flipped to the honest model: the **git-tracked project file is THE registry** (the
  editor reads/writes only it), and the deployed copy is a **build artifact** like the DLLs — regenerated
  atomically on every Save, recreated on load after a game reinstall, never read back. Hand-edits to the
  artifact are detected and warned about once per session (the next Save overwrites them); a one-time
  per-machine migration adopts pre-collapse deployed state into the source, and a missing source adopts the
  artifact (a fresh clone against a live install loses nothing). Artifact-refresh failure is loud but never
  fails a Save — the source is safe, the game just runs stale until the next success. Scope: the model
  registry; districts/formations keep the old pattern as follow-up candidates. **DRILLED same evening, all
  five steps**: Save advanced both files in the same second, byte-identical; a planted hand-edit in the
  DEPLOYED file drew the build-artifact Console warning; deleting the deployed file had it recreated on the
  next Refresh **from the edited source** (proving authority — the artifact hand-edit was wiped exactly as
  promised); and a planted external edit of the SOURCE raised the coherence banner, closed by ↻ Reload. The
  drill also caught one label straggler: the Lab's footer still named the deployed path — fixed to name the
  source.

- **OFFSITE BACKUP: VERIFIED END-TO-END — AND SELF-RECOVERING (2026-08-19).** The one backup layer never
  watched succeed finally got its drill: a manual "Back up now" produced a registry-verified snapshot AND its
  count-verified offsite zip (1.06 GB, atomic rename completed). The verify also caught a real gap: the
  morning's daily auto backup's zip had **died mid-write when a recompile killed the background thread**,
  leaving a stale `.partial` and no final zip — silently, with no retry (the atomic design prevented
  corruption but not absence). Fixed: on every editor load, before the daily-auto pass, stale partials are
  deleted and any backup folder missing its final zip is **re-zipped automatically** (count-verified by the
  same core; a reload can never race a live writer because the reload is what killed it). The stale 21:01
  partial doubled as the fix's natural drill — **DRILLED same evening**: the next reload deleted the stale
  partial, re-zipped in the background, and the count-verified final zip (1.06 GB) landed by atomic rename at
  21:10 with zero partials left. Every layer of the backup system has now been watched succeed.

- **THE HAND-LIST GATE (2026-08-19) — the audit's residual risk, closed.** The Factory/Lab ownership-rebase
  lists were guarded only by MAINTENANCE-TRAP comments — a future UI field could still be silently reset on
  Save (the combatZ class). `Tools/check_handlists.sh` now runs the audit's exact mechanics on every push:
  UI-edited fields diffed against each window's re-apply list, any miss failing the push by name with the fix
  pointed at. **Drilled at birth**: planting the historical combatZ omission produced the named FAIL; the
  restore went green; the very push that shipped the gate ran through it. Factory 30/30 covered, Lab 40/40.
  The silent-reset class is now structurally impossible, not merely documented.

- **MULTI-SMR PREVIEW SLICE (2026-08-19) — the known future ambush, closed before it fired.** The preview-
  texture fix persisted ONE atlas-remapped clone while the bake remaps *every* skinned renderer's mesh — a
  single out-param the loop overwrote, so the first multi-renderer rig baked would have replayed the corrupt-
  texture saga on its other parts. Now: the baker persists one clone per renderer (`_PreviewMesh`,
  `_PreviewMesh1`, … — index 0 keeps the historical name so existing bakes stay valid, and the numbered set is
  swept on every re-bake and in the static-over-animated cleanup); **one shared loader** feeds BOTH preview
  windows and all call paths (the pattern-copy lesson applied up front this time — copies grepped, none left);
  each renderer match-and-consumes its clone by vertex count, and the loud log reads `APPLIED n/m` with any
  unmatched clones named. The list-of-one path is exercised by every existing multi-material model; the true
  multi-clone path awaits the first multi-SMR bake.

- **SMOKE TEST, FIVE-POINT UPGRADE (2026-08-19; user call: "can we apply all?").** The F8 harness closed its
  five known gaps in one pass: (1) an **ObjectSpace write-back self-test** — one live pawn entry is mutated
  and re-read through the exact boxed-struct chain every runtime offset uses, so the combatZ died-in-the-box
  class (previously findable only by a battle drill) is now a hard FAIL from one F8 press; (2) the silent
  19-of-22 delta is **named** — uninjected entries are listed with a diagnosis (disabled vs no unit on the
  map); (3) the verdict is written to **`haf_smoke_report.txt`** next to the load and bindings reports — a
  headless/CI launch can now assert all three files clean; (4) **sampler health** — entries whose features
  need the state/combat sampler but hold zero samples are noted (a gate regression is visible without a
  battle); (5) **vacuous-coverage notes** — a green segment that verified nothing says so ("districts authored
  but 0 tiles live — UNTESTED this session"), keeping PASS honest per the silence-is-not-success rule. The
  verdict stays a pure function; 4 new tests (120). **VERIFIED in-game same day**: PASS with `seam write-back ok`, the three uninjected entries named with the benign diagnosis (DugoutCanoe/ReconZeppelin/VolleyGun — no unit on the map), the district UNTESTED note showing, and `haf_smoke_report.txt` written. All five features live.

- **THE STRUCT BATCH (2026-08-19) — derived bindings close the drift net's last silent surface.** The bindings
  census covered 50 named types, but the structs HAF pokes hardest — `PawnEntry` and its `ObjectSpace`/pose/
  bone-rotation slots (the GPU seam written every frame), `Skeleton`/`BoneInfo` (preflight + injection), the
  army/battle walk (the state sampler) — were absent, because the code reaches them STRUCTURALLY (array
  elements, field values) and their names never appear anywhere. The fix follows that fact: each struct is
  **derived from its anchor member** — `PawnEntry` = element type of `PawnManager.pawnEntries`, `ObjectSpace` =
  that struct's field type, and so on — the exact path the runtime walks, so the census has zero name-guessing
  and zero false-positive risk. Nine derived entries (+ widened members on three existing ones; inventory was
  mechanical — every `GetMember`/`SetMember` literal grouped by receiver). A game update that renames an anchor
  reads `[MISSING TYPE]`; a reshuffled struct member reads `[MISSING MEMBER]` — one named line in
  `haf_bindings_report.txt` instead of torn skinning or a silently dead offset (the combatZ write-back's own
  seam is now censused). Host-proven with 3 new tests (115 total): derivation across field/array/generic/
  non-public/property anchors, broken-anchor → null-not-throw, derived types flag members like any Dep.
  **GAME-VERIFIED same day — and the first launch was its own drill:** all nine derived structs resolved
  (`missing_types=0`), while the report flagged **three members I had attributed to the wrong receivers** — the
  A1 lesson relearned live, caught by the report's self-validation exactly as designed (`OutputLayerInstance`
  belongs to the atlas-dump walk's content entries, not `PawnEntry`; `AttackerGroup`/`DefenderGroup` to the
  SIMULATION battle the war-cry hook receives, not the presentation battle). Re-homed via three more derived
  accessors — including a method-parameter derivation for hook types — plus the war-cry chain censused. Final
  verified state on game 1.30: **`resolved=63/63  missing_types=0  missing_members=0`**, F8 Smoke Test PASS
  with `bindings ok` folded into its verdict. 116 tests. From this launch on, a Humankind patch touching any of
  these structs announces itself by name at boot.

- **v0.1.0 — THE FIRST TAGGED RELEASE (2026-08-19; withdrawn to draft the same day).** Both repos tagged
  `v0.1.0`; a GitHub release with an extract-into-game-root zip (plugin + schema DLL under `BepInEx/plugins/`
  + INSTALL.txt) and release notes distilled from this changelog. Everything the preceding weeks built made
  this shippable: CI from public sources, the four-surface pack validator, Ship Status, drilled entry-state
  coherence and backups, the bindings drift net, 112 tests. **Unpublished at the user's request shortly after
  release** — kept as a DRAFT (asset + notes intact, re-publishable in one click); the git tags remain.

- **THE HAND-LIST & LABEL-LIES AUDIT (2026-08-19)** — backlog #4, the last open entry-state coherence item,
  executed mechanically rather than by eyeball: every field the Factory/Lab UI edits was extracted by pattern
  and diffed against every hand-maintained list (the Factory ownership rebase, the Lab ownership rebase, the
  bake-config capture), and every "runtime / no re-bake / applies on load" claim was read against its actual
  code path. The lists came back **complete** (34/56/29 fields, zero uncovered) — the combatZ drill-catch the
  day before had already fixed the one real hole. Three findings, fixed same day: **Make static left
  gunElevMax/gunElevAxis/animPhaseSpread alive** (gun elevation is runtime-applied to every non-donor entry, so
  a made-static gun kept its elevation behavior — precisely the "cursed leftover" class Make static was built
  to kill); the **Save-settings status** claimed Position offset/Size apply on load — false for static entries
  (now says which fields are baked, per entry type); **Browse's auto-set of animUnitFix** is discarded by Save
  settings (animation-owned) — the status now discloses it. One stale specimen retired: the tris slider already
  discloses its double-sided halving in tooltip and bake log. Residual, accepted: the hand-lists are guarded by
  MAINTENANCE-TRAP comments, not by a gate.

- **COMBAT HEIGHT OFFSET — the diving submarine (2026-08-19, user-designed).** "It would be cool that in combat
  they would be actually submerged": new shared field **`combatZ`** (schema field 67; 0 = off) — world units
  added to a unit's height while its army is battle-locked (deployment → resolution), negative dives, positive
  lifts, **eased 2s both ways** via a combat-flip timestamp carried in the state samples. Works for STATIC and
  animated entries alike: statics bake their Position offset into the mesh, but a state-dependent offset can
  only ever be runtime — this is their one legitimate runtime translate, applied at the same proven per-frame
  ObjectSpace seam as everything else. Combat stance comes per-pawn from the battle-lock sampler the
  state-driven clips already read; its gate now admits `combatZ` entries, so a plain static sub joins sampling.
  Authoring: a Flight-character slider plus an **"In combat" preview toggle** — the model drawn at battle-locked
  height with the keel/top readout following, which is how the submarine was calibrated to snorkel-only trim
  (top +0.05u vs the crest-inclusive waterline; `combatZ` −0.13). Validator range rule + test (112 tests);
  parity green at 67 fields/80 parsed keys. **Editor-side drill caught a real bug the same hour:** Save reset
  the new field to 0 — `RebaseLabOwnedOnRegistry` re-applies only a hand-maintained Factory-owned field list,
  and the new field wasn't on it. Fixed, and the list now carries a MAINTENANCE TRAP warning (a new Factory
  field needs: schema, regex fallback, UI, and that list — the parity gate does not check it; same silent-reset
  family as the label lies). **DRILLED same day — and the in-game drill caught the third
  last-line-of-the-pattern omission in two days:** the first battle showed NO dive despite the engaged log,
  because `ApplyCombatZ` copied `ApplyPositionOffset`'s boxed-struct pattern but dropped its final
  `SetMember(entry, "ObjectSpace", os)` write-back — the offset was computed and logged, then died in the box
  (the log proved the COMPUTATION, not the WRITE; the user's flat "I did not see any change!!" was the accurate
  instrument). One line fixed it; second battle verified: snorkel-only above the swell, hull a shadow beneath,
  eased dive and resurface. The pattern now has three drill-caught members (the Lab-port call site, the
  restore-path substitute, this write-back) — when copying a working pattern, its LAST line is the one you
  drop, and only executing the scenario notices. Also same day: preview zoom-in deepened 5× (0.1 → 0.02
  minimum distance factor) for close-up trim inspection.

- **VEHICLE LAB POLISH (2026-08-19, both user finds).** (1) The **Static tracks** isolation switch moved to the
  top of the Spin section and now gates the tread dials (speed/detail gray out when the tracks won't run —
  decision before dials). (2) **Save recipe… kept reverting to the raw model's name**: saved as `prod3`, the
  next save suggested `prod2` again — the dialog default derived from the source file every time, while the
  window already tracked the current recipe name for its combobox. It now defaults to the tracked name,
  falling back to the source-derived name only for a never-saved session.

- **ANIMATION LAB PREVIEW TEXTURE-CORRECT (2026-08-19)** — the user caught the day-old Factory first-select
  texture fix stopping at the Factory's window: the Lab's fit preview has its own copy of the renderer-flattening
  loop and still paired original FBX UVs with the packed atlas on load. The substitution was ported — and the
  port itself was drilled into a second finding: it landed in the rebuild path while the domain-reload *restore*
  path (the one that runs right after a compile) still drew unsubstituted. Both fixed, user-verified. Postmortem
  epilogue added: a fix in copied code needs grepping for the copies, and a fix has as many deployment points as
  its code has call sites.

- **THE WATERLINE, CALIBRATED (2026-08-18) — vessels now preview at the game's true water level.** The submarine
  that "looked right in preview, near-invisible in game" unravelled into a measured constant: the game floats
  naval pawns with the mean water surface **~0.05u above the model origin**, while the preview's plane sat at
  origin height — every vessel previewed slightly high. Chased methodically: bake logs proved the −0.2 offset
  was in the shipped mesh; the runtime was exonerated (static offsets bake into the mesh, no runtime add); a
  false start blamed a stray builder part (the "floating strips" were the hull top — a real Jagdpanzer is 2m
  tall); unit scaling was ruled out by the era grid (Era5+ rows all 1.0). The decisive instrument was built
  mid-hunt: a **keel/top numeric readout** in the preview header (stale bake reads keel 0.00; wrong plane reads
  the right keel under the wrong picture) plus the user's calibrated cruiser — hull paint marking the true
  waterline — converged the constant stepwise (0.5 → 0.1 → **0.05**, the *water @* dial, EditorPrefs-stored,
  measured-on-the-map tradition like the 6.93u tile). The residual: the sub "matched only at 0.15" — that 0.1
  is **wave amplitude**, confirmed in-game (long swell dynamically claims the deck); low hulls lose real
  freeboard to crests that a flat plane can't render — the dial doubles as a crest-state preview. Verified
  in-game: the sub now rides deck-awash, superstructure clear, matching the preview. Discovered en route, on
  the audit list: the runtime-fields help text says position "applies on load" — for STATIC entries it is
  baked and silently needs a re-bake (label lie, the backlog #4 family). **Epilogue — one number, one home:**
  the constant lived in three places within an hour (EditorPrefs dial / code default / docs — the glbconv
  split-brain in miniature, user-spotted), so it landed as **pack configuration**: `waterLevel` in the
  registry header next to `unitScales`/`eraGrid` — versioned, dual-written, backed up, shown read-only in the
  preview, no UI can change it; the dial was retired the same day it was born. Mechanism explained too: the
  game's own ship meshes anchor at the WATERLINE while HAF bakes anchor at the KEEL — every vessel's negative
  Z has been re-creating the draft that convention difference removes; 0.16 = rendered-surface offset + wave
  crest. All three affected vessels' Z recalibrated (sub −0.04, cruiser −0.23, tank destroyer 0) and verified.

- **SHIP STATUS — "baked but not built" made visible (2026-08-18).** The boot pre-flight's first real run caught
  it live: the HandCrankedSubmarine re-bake (19:34) postdated the last mod build (19:29) by five minutes, so the
  game resolved a dead skeleton GUID — the exact "was it baked and shipped?" trap the validator names. Nothing
  in the editor surfaced which bakes the game hadn't seen, so now two things do: an inline notice in the Model
  Factory on the selected entry, and a **Tools ▸ HAF ▸ Ship Status** window listing every entry against the
  newest build (BAKED-NOT-BUILT / BAKE-MISSING / ORPHANED-BAKE / shipped / no-bake-needed), both driven by one
  shared core that reuses the baker's own output whitelist so it can never drift. Bonus finding from the same
  scan design: orphaned bakes (outputs left by renamed/removed entries) still ship as dead bundle weight — the
  window lists those too. **Its first run was its own drill**: the scan knew only the unit registry and accused
  every district and prop bake of being an orphan (user screenshot) — fixed by teaching it all three registries
  (units, districts, props) + hand-prop references, each row labelled with its kind; ConversionGateTest's
  `__convgate__` debris got its own TEST ARTIFACT label. Then, per three user requests in one sitting, the list
  became a cleanup tool: any row with baked outputs is selectable (plain click / Ctrl-toggle / Shift-range, the
  checkbox and Tick all drive the same state), and **Delete selected** sweeps via the baker's whitelist with the
  delete-guard snapshotting every file — owned entries are only un-baked, never removed. Full page:
  docs/Ship-Status.md. Same day: the preview's tile hex went **double-sided**, so a boat's waterline stays
  visible from below the surface — user-verified as the way to judge how deep a vessel should sit. (And a
  floating tank destroyer that looked like a grounding bug turned out to be the live-offset display working
  exactly as designed: a Position-offset Z of 0.5 — the waterline axis — authored back when the bake sat SUNK
  in the ground, i.e. a manual compensation that auto-ground later made redundant and turned into a float.
  **The compensating dial outlives the defect it compensated for** — same family as the 2×-height helicopter
  offset of 08-07; when a bake-level fix lands, every manual dial compensating the old behavior becomes a live
  error with no alarm. The ground/waterline reference + live-offset preview is what makes this class visible
  at a glance now — a registry-wide offset audit found all remaining verticals deliberate: flyers at altitude,
  vessels below the waterline.)

- **ENTRY-STATE COHERENCE (2026-08-18) — the "serious configuration bug" of 2026-07-26, structurally addressed.**
  An entry's config lives in four places (two window forms, the deployed registry, the project dual-write copy)
  and the reconciliation ambushed the user for weeks. Built per the backlog's recorded impact order: (1) the
  Factory gets the **Lab's Form ≠ registry banner** — surviving form compared on every reload, explicit choice
  (↻ Reload entry / Save / Bake), never a silent resync — and the cross-window nudge is now **coherence-aware**
  (a Backup-window restore raises the banner instead of silently reloading an edited form); (2) the **bake-time
  model-file confirm** — a stale form file that differs from the saved entry's asks loudly with both paths shown
  (the translation-cube-over-Jagdpanzer ambush, dead); (3) the **SelectEntry funnel** — every selection change
  (popup, Remove, Undo, banner-reload) routes through ONE path updating dropdown + form + preview + coherence
  flag atomically, structurally retiring the 08-16..18 stale-window family (whose four bugs were each one
  forgotten surface at one bypassing site; Clone is the one documented deliberate bypass). **Self-review before
  ship caught three defects**: the Lab's own spurious-banner lesson unlearned (OnGUI's `animated` self-heal must
  be mirrored onto the registry copy before comparing), an entry *removed* under the window reporting "no
  difference" (now maximal difference), and Clone inheriting a stale banner whose Reload would wipe it. The
  two-pack.json design is now documented in Factory-Manual; "Make static…" already covers the animated→static
  path. **DRILLED same day — all five drills passed, and the drill caught a fourth defect the review could not:**
  the vanished-entry banner (drill 3: Bears hand-removed from pack.json) never fired, because `RefreshList()`
  re-derives the dropdown index by name and resets it to 0 when the entry is gone — so the compare's
  `selected <= 0` guard swallowed EXACTLY the case the review-fixed `reg == null` rule existed for. Two
  individually-correct mechanisms cancelling each other: structurally unreachable, invisible to reading,
  instant under fire. Fix: the form carries its own serialized identity (`loadedName` — which registry entry
  it was loaded from / last saved as; empty for `<New>`/clone), the compare keys on it instead of the volatile
  index, and the banner's Reload uses it too (a half-typed rename reloads the ORIGINAL entry). Verified by
  re-drill; a per-reload Console evidence line (`loadedName` + differs) now makes any future missing-banner
  report diagnosable instead of guessable. The ADR's lesson, proven a second time in one week: the defect was
  in the interaction between two reviewed-correct parts — only executing the scenario finds those. **Drill
  follow-ups:** the Refresh button is now the on-demand coherence check (user design — re-reads the registry
  and raises/clears the banner immediately, no recompile needed; the form is still never touched without the
  explicit Reload choice), and the post-drill diff review caught that the drill-1 test edit (pawn `…_01a`, a
  nonexistent unit) had been SAVED during the drill flows — reverted in both registry copies, Bears restored,
  both copies verified to hold the same 23 models. Drills leave fingerprints; always sweep the registry after.

- **FIRST-SELECT PREVIEW FINALLY TEXTURE-CORRECT (2026-08-18)** — the user's "number one problem with this
  editor," deferred since 08-01: selecting a model showed it mis-textured until the next bake. **The root cause,
  finally pinned to a line:** `BuildMultiAtlasAndRemap` remaps the rig FBX's skinned-mesh UVs into the packed
  atlas **in memory only** (clones assigned onto the imported asset) — so the preview is correct right after a
  bake and reverts to ORIGINAL-UVs-vs-packed-atlas on any reimport or editor restart. Explains every symptom of
  the bug's whole history, including why the "never force-reimport the FBX" rule existed. **First attempt
  (preferring the bake's `_Preview.prefab`) was reverted within the hour** — user drill: "why is it heading up
  without a surface?" — that prefab is a display-flipped bind pose with no ground plane. **The real fix:** the
  bake already persists the remapped clone (`_PreviewMesh.asset` — same FBX-space geometry, atlas-remapped UVs);
  `LoadPreview` now *substitutes* it for the renderer it was cloned from **inside the upright, grounded FBX
  route** — correct texture, same faithful view. **Second attempt also drill-caught within minutes** (still
  corrupt): the name-based match could never fire — `CreateAsset` renames the persisted mesh to its filename —
  so the substitution silently did nothing. Final version matches by **geometry identity** (identical vertex
  count on a skinned renderer, used once) and prints a loud `APPLIED` / `NO MATCH` Console line per preview
  load, because a silent no-match is exactly how the first two versions hid their failures. **Drill-verified by
  the user: "finally it looks correct."** Three versions, two caught by drills — the ADR working as written.
  **Ship-safety re-confirmed throughout:** display-only either way — the shipped GPU mesh always carries the
  remapped UVs (`draw_mats.txt` proof, 08-01, and every in-game verification since). The preview was lying; the
  mod never was. **Why it survived six weeks of fixes — the full retrospective (six protective mechanisms,
  general lessons): [Preview-Texture-Postmortem](docs/Preview-Texture-Postmortem.md).**

- **PACK PRE-FLIGHT VALIDATOR — silent content failures become named messages (2026-08-18).** The
  designed-not-built tool from the 08-02 external review, built exactly per
  [Pack-Validator-Design](docs/Pack-Validator-Design.md): a wrong bone name (`muzzleBone: "Turrret"`), a missing
  WAV, an unbaked clip GUID, or an out-of-range dial used to just… not happen. ONE pure rule set in the shared
  schema DLL (`Haf.Schema.PackValidator`: ~30 rules — file existence + format, bone-name existence, pawn-name
  reality, `x,y,z`/`a,b,c,d` formats, every documented numeric range, the state-driven mutual exclusions — with a
  tri-state context: a host that can't answer a lookup SKIPS the check, never guesses), consumed by two thin
  hosts: the Model Factory's **"Validate pack"** button (pre-ship: pawn names from the Pick list, files in the
  deployed pack, bones from each entry's baked skeleton asset) and the plugin's **boot-time pass** (once per
  process after registration: bones against the LOADED skeleton, files on the *player's* disk, authored GUIDs
  that didn't resolve — appended as `## Pre-flight` to `haf_load_report.txt` with one summary log line).
  Warnings EXPLAIN, nothing is blocked — the fail-soft rule stands. 19 rule tests; suite 92 → **111**.
  **DRILLED same day, and the drill earned its keep before passing:** three faults planted in the live pack (the
  design's own `"Turrret"` bone typo, a misspelled WAV, a volume of 5) → first result "validate detects nothing" —
  a **silent failure in the Validate button itself** (no try/catch), exposed by running the same core on the same
  file headlessly (which named the fault instantly). The validator failing invisibly is the exact disease it
  exists to cure; fixed with loud exceptions, the validated registry path printed even on clean runs, and (drill
  feedback) results in a dialog instead of only the Console. Second run: **all three faults named with field,
  entry, and reason — drill passed**, pack restored byte-identical. The ladder held again: written → reviewed →
  drilled → trusted.

- **AUTO-VERSIONING + DELETE GUARD (2026-08-17, user: "auto backup, especially when I remove assets… also
  configuration… go back versions").** Two silent, optional guards in `BackupAuto.cs`, both feeding the same
  restorable backups list: a **delete guard** (an `AssetModificationProcessor` snapshots any asset under the
  protected roots to `_deleted_<timestamp>/` BEFORE any deletion — Factory Remove, Project-window, script — then
  lets the delete proceed) and a **daily auto-version** (first editor load of the day runs the full backup — assets
  AND configuration — through the same core as the button, so it gets a Restore button like any manual version;
  newest 3 kept, rotation logged; manual/_deleted/_prerestore never auto-deleted). Stricter side effect: a
  COUNT-MISMATCH backup now aborts a restore's pre-snapshot and skips the offsite zip instead of proceeding on a
  suspect archive. Headless-compile-checked (Roslyn gate). **Critically reviewed the same hour, four real
  defects fixed pre-ship:** (1) guarding `Assets/Resources` would have FLOODED the backup root — the bake
  pipeline delete-firsts baked assets on every re-bake (~30 `AssetDatabase.DeleteAsset` sites) — dropped from the
  protected roots (bakes are regenerable; the daily auto still versions them); (2) same-second deletes of
  `Tank.png` + `Tank.mat` collided into one folder, silently overwriting the first manifest — extension kept +
  counter-uniquified; (3) the 1+ GB daily auto copied synchronously on editor load (~30-60 s "hang") — moved
  wholesale to a worker thread (pure file IO); (4) delete-guard snapshots had no `SRC` manifest, so their Restore
  button was dead — now a one-click restore incl. the `.meta` (GUID preserved, references survive). **And a
  fifth, user-spotted during the recovery drill: restore was ALL-OR-NOTHING** — recovering one group from an
  older snapshot rolled every other group back to snapshot time (an old backup got more dangerous to restore the
  older it grew). Fixed with **selective restore**: the same group checkboxes that scope a backup scope a
  restore; the confirm dialog states the scope; `_deleted`/`_prerestore` snapshots still restore whole. **The
  drill kept giving: two more user-found issues, fixed live.** (a) Remove left the PREVIEW rendering the removed
  model (stale-state, same family as the sel-reset bug) — cleared. (b) Recovery required knowing the Backup
  window exists and having a backup that happened to cover the moment — Remove is now **recycle-bin semantics**:
  it snapshots the entry JSON + the exact baked-output whitelist to `_removed_<ts>_<name>/` BEFORE deleting
  (aborts if the snapshot fails — never destroy what can't be restored), and an **Undo remove** button appears
  right where Remove is (user-designed placement), restoring registry entry + baked assets in one click. **And a
  third: "restored 1628 files!!!!"** — the blanket copy alarmed exactly the person it was reassuring. Restore is
  now **smart**: byte-compares each file and writes only the missing + actually-changed ones (identical files
  untouched — also sparing Unity ~1,600 pointless re-imports), reporting all three counts. **And the drill's
  biggest catch, found the hard way ("the restore FAILED!!!"): the backup NEVER CONTAINED the model registry.**
  The config group still captured the pre-multi-pack `haf_*.json` root files; the registry moved to
  `haf_packs/<mod>/pack.json` and the group was never updated — so the restore brought back all 28 baked files
  but had no registry entry to restore. Recovered by re-inserting the entry verbatim from the git-tracked
  project registry copy (both registries re-validated: 22 models, parse-clean); `haf_packs/` added to the
  Runtime-config group so every future backup carries the real registry. The honest lesson: a backup's contents
  were asserted from its group NAME, not verified — the same claim-vs-check gap the smoke test was built to
  close, now closed for backups too. **The drill's final round (same evening):** critical-content verify (a
  backup missing the registry marks itself NOT ok; green says "registry verified in snapshot"); `_removed_`
  snapshots fully restorable from the window itself (shared core with the Factory's Undo button, which now also
  selects + loads the restored entry); the list grouped into counted foldouts with date-time-first rows
  (delete-guard open by default, user-tuned); preview-scratch churn (`_PropFit`/`_Preview*`) excluded from the
  delete guard; restores auto-refresh open Factory windows (the restore "didn't work" — it had; the dropdown was
  stale); tooltips on every button; the list fills the window height. **Thirteen user-driven fixes in one
  drilling session — and the process lesson became an ADR: a tool is not trusted until it is DRILLED.**

- **OFFSITE BACKUP — the last total-loss scenario closed (2026-08-17).** The Backup window gains an optional
  *Offsite folder*: every backup is also written there as ONE `HAF_<timestamp>.zip` — silent (background thread,
  a multi-GB FactorySource snapshot no longer freezes the editor), atomic (`.partial` → rename), never
  overwritten, and self-verifying (the zip is re-opened and its entry count compared against the snapshot; a
  mismatch deletes the partial loudly). Point it at a cloud-synced folder and the licensed source models + bakes
  — the only irreplaceable, un-git-able half of the project — survive a machine-level event. Auto-zip toggle for
  set-and-forget; a manual button covers pre-existing snapshots; `_prerestore` safety snapshots deliberately stay
  local. (Editor-side; compile-checked headlessly via the Roslyn gate, whose `.rsp` gained the
  `System.IO.Compression` pair + a defensive `Assets/csc.rsp`.)

- **SHARED-SEAM CENSUS — the first mod-conflict guard (2026-08-17).** Pack-vs-pack conflicts were always guarded
  (declared overrides, first-loaded-wins, loud logs — ADR'd and test-pinned); HAF-vs-OTHER-MODS had hygiene
  (postfix-first, conditional prefixes) but zero visibility. The smoke test now walks every method Harmony knows
  is patched, keeps OURS, and names any that another owner also patches — `"AnimationLoad (also com.other.mod)"`.
  Informational by design (a neighbor isn't an error; Harmony stacks safely) but it's the pre-printed suspect
  list for the day an interaction bug appears. The PASS line gains `N patched seam(s) [M shared]`. Tested for
  REAL: the suite patches a dummy method with two live Harmony instances and asserts the foreign owner is named
  (which pulled Harmony's MonoMod/Cecil runtime deps into `References\` + `fetch-refs.ps1`). Suite → **92**.

- **F8 WINDOW: no more click-through or reflowing text (2026-08-17).** Left-dragging the window panned the map
  under it — the game reads mouse input independently of IMGUI. Fixed WITHOUT camera surgery by speaking the
  game's own language: type-hunting the Managed DLLs (bindcheck-style MetadataLoadContext) found
  `Amplitude.UI.Interactables.UIInteractivityManager.IsMouseCovered` — the public static the game's own windows
  set so map input ignores covered drags. `Hk_MouseCoverExtend` postfixes `SpecificUpdate` (where the game
  recomputes the flag each frame) and ORs in "or over the HAF window" — every consumer that respects the game's
  windows now respects ours. Binding catalogued (bindcheck `50/50`). Also pinned the window to a fixed 520px
  width: GUILayout re-measured width from content every repaint, so the verdict text visibly re-wrapped while
  dragging — deterministic wrap now.

- **F8 SMOKE TEST DEPTH PASS — per-entry assertions, each earned by a shipped bug class (2026-08-17).** The
  in-game smoke verdict was a coarse gate (bindings ok / error count / models > 0); user verdict: "add more tests
  to make it really meaningful." It now also asserts, per INJECTED entry: **dead clip roles** (a role GUID
  authored in the registry whose animation never resolved — the howitzer's "shipped a dead idle-override GUID"
  becomes a named FAIL instead of a unit quietly failing to deploy), **missing assets** (skeleton, or an authored
  atlas that didn't load — the organ-gun-red class gets a named cause), **failed configured sounds** (checked
  once the audio poll has tried), and a **GPU-wall alarm** (any mesh layer ≥95% verts/indices — the silent
  skin-vanish wall, alarmed before it hits, via a structured `ReadMeshBudget` now shared with the F8 display).
  The verdict stays a pure function (`SmokeFacts` → `SmokeVerdict`) so every new fail class is unit-pinned —
  a PASS now states what it checked ("deep checks clean on N injected"). **The first live run earned its keep
  twice**: it flagged `Retex_…StealthCorvettes` "missing skeleton" — a FALSE POSITIVE (a retexture-only entry
  legitimately has no skeleton; corvettes verified fine in-game), which forced the per-entry gathering into a
  pure `GatherEntryFacts` with every asset check gated on *authored* GUIDs — and the refactor's first draft
  itself shipped the exact `cb`/`cbb`-class wiring typo the review had warned about (`e.ald` doubled, `e.alc`
  dropped). Both are now test-pinned: a retexture-entry case plus a **36-component wiring theory** asserting
  every GUID component of every role arms its dead-role check alone. And because an instant PASS "didn't feel
  like real testing" (fair — the deep pass reads outcomes the load pipeline already established, so speed is
  inherent), the PASS line now **shows its work**: it prints how many facts it verified ("verified 47 clip
  role(s), 17 asset(s), 12 sound(s), 3 GPU layer(s)") — auditable against the registry instead of asking to be
  believed. **Scale-out (same day):** the deep pass now also covers the axes the smoke test never looked at —
  **districts** (per `haf_districts.json` entry: fxMesh GUID parsed, authored ground-material NAME resolves;
  live tile count in the PASS line), **texture-only retexture skins**, and **hand props** (authored →
  layer + atlas must exist). All data-driven off the registries, so every future unit AND district is covered
  the day it's added, no test code. Fault-injection round proven live the same day: a flipped atlas-GUID digit
  and a renamed WAV both came back as named FAILs on the first F8. **Loose-file sweep (same day, user: "basically
  any loose file"):** every disk file any entry references (all 7 sound roles + the skin PNG) is now
  existence-checked for ALL entries — injected or not — with the loaders' exact search order (pack `assetDir`
  first, legacy shared dir second), closing the hole where a missing WAV for a unit absent from the current save
  smoke-tested green; a missing-on-disk file reports once (the derived load-failure line is deduped). Suite
  63 → **90**.

- **CI — every push now builds + runs the full suite, with zero game files (2026-08-17).** The blocker was
  always the gitignored `References\` DLLs; the unlock was discovering the `Amplitude.Mercury.Animation.dll`
  reference was **vestigial** — every Amplitude touch in the plugin is string-based reflection, so the csproj
  reference was simply dropped and the build stayed green. Every remaining reference has a public home:
  Newtonsoft 11.0.1 (nuget.org), `BepInEx.dll`+`0Harmony.dll` (the official BepInEx 5.4.21 release zip), and
  the UnityEngine modules from **unity.bepinex.dev** — BepInEx's mirror of *runnable* unstripped Unity
  assemblies, version-exact 2021.3.1 (the nuget `UnityEngine.Modules` reference assemblies compile but throw
  `TypeLoadException: internal call with non-NULL RVA` the moment tests load them — found the hard way, 52/61
  red). `tools/fetch-refs.ps1` collects all 12 DLLs (never overwriting game-copied ones; game copies win), and
  `.github/workflows/ci.yml` runs fetch → build → 61 tests on a clean runner. Proven by full local simulation
  first: fresh clone, no References, fetch, build green, **61/61 pass**. bindcheck stays manual — validating
  bindings genuinely needs the game's own DLLs.

## Units & animation

- **GLBCONV SPLIT-BRAIN — a verified fix silently regressed out of the deployed exe (2026-08-17).** A verified
  critical review found glbconv had TWO sources of truth that had each grown a fix the other lacked: ENCReload's
  `Program.cs.src` (Jul 12) alone held the **T5 mirrored-winding fix** (`GetDeterminant() < 0` → swap B/C so
  scale-(-1,1,1) vehicle halves wind outward), while this repo's `baker/glbconv/Program.cs` alone held the
  **multi-tile UV warning** (critical-review #6). The 2026-08-16 exe rebuild (ENCReload d6017cb) was made from the
  baker copy — so the deployed converter shipped with **T5 regressed**: mirrored halves of symmetric vehicles would
  render inside-out again. No gate caught it because nothing compared the two sources. Fix: T5 merged into
  `baker/glbconv/Program.cs` verbatim; rebuilt against the same committed `SharpGLTF.Core.dll`; **A/B-verified** —
  byte-identical OBJs on 4 FactorySource models (no mirrored nodes → no side effects) and a synthetic
  two-node mirrored .gltf proving the deployed exe kept inward winding (`f 4 5 6`) where the merged build swaps
  B/C on exactly the mirrored node (`f 4 6 5`); redeployed to `ENCReload/Tools/glbconv/`. **Structural fix:
  `Program.cs.src` deleted — `baker/glbconv/` in this repo is now the ONLY source** (BUILD.md rewritten to say so,
  with the A/B-verify-before-deploy procedure). Lesson for the record: every cross-repo file copy without a sync
  guard eventually ships a regression; this was the one that did. **Same-day follow-up:** the stale `baker/`
  Blender-script copies (`rig_anim.py` / `vehicle_rig.py` / `deploy_convert.py` + all of `baker/Tools/` — labelled
  "live", never executed by the pipeline, weeks behind `ENCReload/Tools/`) were **deleted** — same disease, same
  cure: one home per file. Verified end-to-end same day: Bake Smoke Test 5/5 (both static paths through the new
  exe), F8 in-game smoke PASS (0 injection errors), tank + Cobra visuals clean. **CLI hardening (same day):**
  a usage error and a non-numeric grid arg now exit 2 with a named error (the old `void Main` returned exit 0
  on bad usage — "success" to any caller); rebuilt, A/B-verified byte-identical on 3 models + the mirrored-node
  winding probe, redeployed.

- **SAVE-RELOAD ISOLATION — the organ-gun load-order bug (2026-08-16).** Loading a heavy save then another in
  one app run tore an animated custom unit (the organ gun) and, once the mesh bound, painted it the wrong donor
  **red**; a fresh load was always clean. The F8 GPU-mesh-buffer readout (a `+1 mesh / +4.7k verts` diff on the
  second load) ruled out buffer overflow and pointed at stale isolation, and a per-load registration dump found
  the cause: **`AnimationManager.AnimationLoad` fires once per PROCESS, not per save-load** — the whole
  model-axis re-arm hung off it, so a second session never re-registered our skeletons into the game's rebuilt
  `AnimationManager`. Fix: re-arm on the seams that *do* fire per session — **`PawnManager.Load`** (the universal
  one: save-load, reload, *and* a New Game) plus `Sandbox.Load` (so the district axis resets synchronously) —
  all via a thread-safe flag consumed on the main-thread `Update` (the hooks may be off it). A `[SessionProbe]`
  proved the whole thing: `AnimationLoad` fired only once even across a main-menu trip, and a New Game after a
  load re-registered (fresh skel ids) only once `PawnManager.Load` was wired in. That exposed a second, older
  trap: the re-arm cleanup `Destroy()`'d `e.tex` unconditionally,
  but for a normally-textured model that is the **shared bundle atlas** from `AssetDatabase.LoadAsset` —
  destroying it made the reload's `LoadAsset` return `null` (the red skin). Fix: `ModelEntry.texOwned` — only
  destroy textures the plugin creates, never a `LoadAsset`'d asset. Both verified in-game on the load-order
  repro. Bonus: the model-axis session cleanup (audio/deploy/state maps) now runs on *every* reload, not just
  the first.

- **128-vs-256 BONE WALL — doc reconciliation + a cold case closed (2026-08-16).** A documentation critical
  review found the bone-limit stated as *both* "256" and "128" across three docs. The **128-bone-INDEX wall is
  correct** (per-vertex bone indices break past 127; T-62-proven, deploy code uses a 124-bone wall) — the "256"
  figure is stale. Reconciled `Animation-Pitfalls`, `Factory-Manual`, and `Animated-Models` to 128 (index 127),
  the mech's count to **222**, and the fit mechanism to the deploy path's **pair-merge to ≤126**. This
  retroactively **closed the 26-day "mech wings UNSOLVED" cold case**: rig_anim had slimmed the mech to 222
  bones (under 256) and the wings *persisted*, which was read as "256 disproven, cause unknown" — but 222 is
  still over the real **128** wall, so bones 128–222 (the arm chains = the "wings") were always going to
  collapse. No engine-import decompile needed; the culprit was the GPU skin's per-vertex bone-index ceiling.

- **SHARED SCHEMA — 64 duplicated fields de-duplicated into one library (2026-08-16, verified end-to-end).** The
  `ModelDef` (editor, 128 fields) / `ModelEntry` (plugin, 148) god-object stored ~66 behavioral/sound/prop/tint/transform fields (incl. pawnDescription + the position Vector3)
  IDENTICALLY, hand-synced across two repos + two parse paths (the drift the schema-parity guard exists for). Those 64
  now live once in a shared netstandard2.0 `Haf.Schema.HafModelSchema` that both classes **inherit** — so the field
  can't drift, and (because they inherit) the hundreds of `e.<field>` hot-path uses + object-initializers didn't change.
  A POC first proved the mechanism (Newtonsoft + Unity `JsonUtility` both serialize inherited-from-DLL fields); then it
  was executed and **verified end-to-end**: plugin builds + 59 tests + loads in-game with the new `Haf.Schema.dll`
  dependency + injects all 22 units unchanged; the editor compiles and a Save round-trips all 66 fields (0 wiped).
  `tools/deploy-plugin.sh` ships both DLLs (a redeploy can't drop the dependency). **Deliberately partial:** the GUID
  fields are stored in different shapes (`int[]` vs `sa/sb/..`, a runtime choice) so they stay divergent under the
  parity guard — the worth-it slice, not a forced full merge. See docs/Shared-Schema.md.

- **HEADLESS BINDING DRIFT CHECK — reflection-drift net, step 3 (2026-08-16).** The in-game `haf_bindings_report.txt`
  still needed a launch to read. `bindcheck` (a net8 tool, `Tools/bindcheck/`, using `MetadataLoadContext`) now validates
  the whole `GameBinding` catalog against a Humankind build's assemblies **without launching the game** — it reads
  `Patches/GameBinding.cs` directly (always in sync, no manifest to stale) and inspects the game DLLs reflection-only, so
  Unity's native deps and static ctors are irrelevant. `Tools/check-bindings.sh [<Managed>]` builds it once and runs it;
  a game patch's binding breakage is now named **headlessly** (CI-able on a version bump) instead of found by launching.
  Separate trigger from the pre-push gate on purpose: that guards HAF *code* changes, this guards *game* changes.
  **Verified both ways:** `49/49` clean on the pinned `1.30` build, and it correctly flags an injected fake binding
  (exit 1). Closes the maintainability review's #3 (binding half).

- **DECISIONS (ADR) LOG + backlog triage — shrinking the bus factor (2026-08-16).** The maintainability review's #4.
  Added [`docs/Decisions.md`](docs/Decisions.md) — short records of the *settled* decisions and the *why* behind them
  (pack order follows HK's mod order & why the base-flag was rejected; make-drift-loud over removing reflection; the
  Factory/Lab ownership split; the declined `ModelEntry` POCO split; pair-merge vs slimming for >127-bone rigs;
  rotation-only animation; framework-neutral naming; first-loaded-wins conflicts; the focused-test stance) — so the
  tribal knowledge that would otherwise be reverse-engineered from the code has one home, linked from the docs index +
  `llms.txt`. Also triaged the backlog: recorded the reflection-fragility A5 progress against the GameBinding-gaps item
  (narrowing it to the off-catalog district types + the struct-typed surface), and gave the `rotorSpin` item an honest
  status (parity now allowlists it, but the Save-wipe of hand-authored runtime-only keys is the real open concern).

- **ONE PRE-PUSH GATE — the fast guards are now un-forgettable (2026-08-16).** The maintainability review's #2: the good
  guards (`dotnet build`, `dotnet test` ×59, the Roslyn editor compile-check, the 4-path registry schema-parity) existed
  but ran manually, one at a time, across two repos, with no enforcement. Now one **`Tools/check.sh`** per repo runs its
  fast guards and prints an aggregate PASS/FAIL, wired as a version-controlled **pre-push hook** (`git config
  core.hooksPath Tools/git-hooks`) so a broken build / failing test / drifted schema can't be pushed. Standing it up
  **immediately caught three latent schema drifts** (exactly the "forgotten check" problem): a wrapper field the plugin
  read but the baker never wrote (`module`/`moduleGuid` — added to `RegistryFile`), two runtime-only keys the guard should
  allowlist (`rotorSpinBones`/`rotorSpinSpeed`), and a `float?` read-cast the parity script mis-classified as a type
  mismatch (its nullable handler covered `bool?`/`int?` but not `float?`) — all fixed to green. Heavy guards
  (deploy golden-master, in-editor Feature Test, the in-game binding report) stay out of the sub-minute gate.

- **MACHINE-READABLE BINDING REPORT — reflection-drift net, step 1 (2026-08-16, verified in-game).** The maintainability
  review flagged game-update fragility as the top structural risk: ~1,475 reflection bindings that fail at *runtime*, found
  by squinting at the log. `GameBinding` already cataloged ~47 game types + their members and validated them at startup
  (A1), but only logged + fed F8. Now `ValidateAndLog` also writes **`BepInEx/config/haf_bindings_report.txt`** every
  launch — game version, verified version, `resolved N/N`, then one `[ok]` / `[MISSING TYPE]` / `[MISSING MEMBER]` line per
  binding — a diffable file (next to `haf_load_report.txt`) that a game patch, or a headless CI launch on a new build,
  turns into one report naming exactly what broke. Also migrated the **first raw-reflection site** onto the catalog as the
  pattern for the rest: `GetRuntimeModules()` (pack order) now resolves via `GameBinding.FrameworkServices` /
  `RuntimeService` instead of a raw `Type.GetType`, and both are in the Catalog (47 → 49). **Verified in-game:**
  `resolved=49/49  missing_types=0  missing_members=0`, both new bindings `[ok]` (no late-loader false positive).
  **Coverage batch 1 (same day):** an evidenced audit of the load-bearing injection path added ~60 reflected members to
  the Catalog (49 → ~124) — `AnimationManager` gained `AnimationLoad`/`RegisterMeshCollection`/`GetPoseTRS` + the
  `gpu*Buffer` fields (re-arm + pose), `PawnManager` its descriptor buffers + `pawnEntries`, the empty `ContentLayer` its
  mesh-buffer + compute-buffer members, and the district Element/Selector/District their level-build members. The report
  validated the lot on 1.30 in one launch (`missing_members=0`) — the self-correcting property: a mis-attribution would
  have surfaced as `[MISSING MEMBER]` on the known-good build.

- **PACK ORDER FOLLOWS HUMANKIND'S MOD ORDER (2026-08-16, verified in-game).** A HAF pack is the content-extension
  of a Humankind runtime module, so packs should load in the SAME order the game loaded their modules — the player's
  own mod order — not an invented alphabetical/base rule. This also retired a dead guarantee: the loader still claimed
  "the base registry loads first, so ENC is protected," but ENC left the `haf_models.json` base slot for
  `haf_packs/ENCReload/pack.json` long ago, so that protection had silently lapsed (zero impact today at one pack, but
  wrong the moment a second pack sorted before `ENCReload`). Fix: read the game's ordered active-module list via
  `Amplitude.Framework.Services.GetService(Amplitude.Mercury.Runtime.IRuntimeService).GetRuntimeModules()` (a `string[]`
  of `Name\GUID\…` in load order; fully reflected + guarded), match each pack to its module (by `moduleGuid`, else
  `module`, else the pack's **folder/file name == the module Name** by convention — computed independently of `modId`,
  since ENC's `modId` is `enc` but its folder/module is `ENCReload`), and sort packs by the module's load-order index.
  `dependsOn`/`loadAfter` still layer on top; an unmatched pack or an unreachable API falls back to alphabetical. No
  pack.json or editor change — ENC maps automatically via its folder. **Verified in-game:** `haf_load_report.txt` reads
  `HK module order: enc #1→ENCReload` (matched its module at load-order index 1, right after vanilla). Critical-review #7.

- **MODEL FACTORY UI clarity + compaction pass (2026-08-16, editor).** Renamed two implementation-leaky labels
  to what the modder actually gets — **"Convert grid" → "Weld & simplify (0 = keep exact)"** (it's glbconv's
  vertex-weld resolution, not a grid; tooltip now points a textured model at "Reduce to ~tris") and
  **"Height-based UVs" → "Height-gradient UVs (untextured)"**; shortened **"Re-spawn after load (borrowed rotor
  fix)" → "Respawn after load"** (the rotor-fix detail stays in the tooltip). Display labels only — the fields and
  registry keys are unchanged, so every existing `pack.json` keeps working. Also compacted the layout: the two
  geometry-reduction knobs share one row; the three shading toggles and the four runtime donor toggles each
  collapse to a single right-aligned row; and both transform vectors render label + X/Y/Z on one line (a custom
  `EditorWindow` defaults `EditorGUIUtility.wideMode` to false, which had wrapped them). Docs synced.

- **glbconv warns on multi-tile / UDIM UVs (2026-08-16, tool).** The OBJ tile-shift normalizes UVs by a single
  integer offset (`floor(min U/V)`), which only rescues a ONE-tile island (the Zeppelin envelope in V 1..2). A
  model that tiles across >1 UV tile (a `.1001-.1005` UDIM camo set) left the other tiles outside [0,1]; the
  single-tile atlas can't wrap them, so part of the skin sampled outside the rect and **silently vanished**. Full
  UDIM consumption stays a deliberately-deferred feature (manual Blender texture-transfer workaround), so this
  doesn't add it — it makes the failure LOUD: a stderr `WARNING` (glbconv stderr surfaces as a Unity warning) with
  the U/V spans + the fix, emitted only when the shifted UVs still reach past one tile. Verified: no false-positive
  on a real single-tile multi-material model; fires on a synthesized 2-tile GLB (`U 0..2`). Critical-review #6
  (source `baker/glbconv/Program.cs`; rebuilt exe deployed to ENCReload `d6017cb`).

- **STATIC bake re-extracts on a changed input (2026-08-16, editor).** The static path's extraction gate skipped
  the whole prep+convert block whenever the OBJ merely existed and 'Reuse extracted' was on (`!reuseExtracted ||
  !haveObj`), so changing the source file, the converter, or a convert arg (grid / strip / reduce / double-sided —
  all of which shape the OBJ) was silently ignored and a stale OBJ re-baked (the "rotation doesn't respond" trap).
  The ANIMATED path already guarded this; the static path didn't. Fix (ENCReload `e85e6c5`): mirror the animated
  path's three busters — `glbconv`/`prep_model.py` mtime, source-file mtime, and a settings fingerprint in a
  `<name>.extract.args.txt` sidecar. No-op when nothing changed. Critical-review #5; editor-verified in-Factory on
  StealthCruiser (tool-newer + args-changed busters both fired on a grid change).

- **MODEL FACTORY rename is a real rename now (2026-08-16, editor).** Editing the Resource-name field of a
  loaded entry and then Save / Save-settings / Bake keyed the ownership rebase + GUID-carry on the *new* name,
  which matched nothing — the rebase early-returned, the carry was skipped, and `Upsert` **added a second entry**
  while the old one and its baked assets orphaned. A rename silently made a duplicate. Fix (ENCReload `170e329`):
  resolve the source by the name the form was LOADED under (`existing[selected]` — the same reliable signal the
  Remove button keys on; null/`<New>` for a fresh or cloned form, so a Clone is never a rename). The rebase +
  carry key on that, so the renamed entry inherits the source's Lab-owned fields + baked GUIDs (Unity GUIDs are
  filename-independent → a no-bake rename resolves in-game with no re-bake); the old entry is dropped after a
  successful Upsert, and a rename onto a name a DIFFERENT model owns is refused rather than clobbering it. Also
  collapses the case-only-rename twin-entry case into one entry. Editor-verified with a `SiegeHowitzersCar` ↔
  `SiegeHowitzersCar2` round-trip: one entry each way, `git diff` of the registry was a single renamed line.

- **DISTRICT selectorGuid guard (2026-08-16, editor).** A re-bake minted a fresh `fxMesh` (delete+create) but
  only *set* `selectorGuid` on selector-bake success and never cleared a stale one, so a selector failure left
  the district Upserting as "Baked ✓" while routing through the scoped path with an old selector against the
  new mesh — a broken district reporting success. Fix (ENCReload `9584b23`): clear `selectorGuid` before the
  (re-)bake so a failure genuinely falls to the legacy path, as the code already promised.

- **DISTRICT CLONE LEAK — critical-review follow-up (2026-08-16).** A full-framework critical review (plugin +
  editor) surfaced that the district axis had the *same* leak class just fixed on the model axis:
  `ResetDistrictSessionState` only **nulled** its runtime `Object.Instantiate` clones (private leaves, cloned
  selectors/output-layers, deep-clone material nodes, the B&W gray albedo), which Unity's unused-asset sweep
  never collects — so every in-session reload leaked a native FxOutputLayer + N cloned FxEvolverMaterials + a
  gray texture per scoped district. Fixed with explicit ownership tracking (never touching `LoadAsset`'d bundle
  assets) and a main-thread destroy queue (the reset runs off-thread via `Sandbox.Load`). In-game verified across
  reloads (`[District] freed N runtime clone(s)`, no district errors).

- **`hideSubPawns` COEXISTENCE — critical-review follow-up (2026-08-16).** The gunship duplicate-pawn hide (keeps
  one pawn, buries the stacked squadron copies) counted per model *type*, not per unit — so a second coexisting
  unit of the same model (yours + an enemy's, or two of yours) rendered **nothing**. Fixed by keying "already
  kept this frame" on unit *position* (a unit's stack shares a spot; a different unit is tiles away). Verified
  in-game with several gunship helicopters on screen at once, each a single clean model.

- **UNIT→ENTRY MATCH UNIFIED — critical-review follow-up (2026-08-16).** Repoint resolved a unit to its entry by
  longest-match on the full `pawnDescription`, but the movement/deploy/state polls used *first-in-registry*
  substring on `coreDesc` (the `_NN`-stripped stem) — so two entries sharing a stem (`Foo_01`/`Foo_02` = distinct
  models) repointed to distinct models but animated/deployed/sounded from whichever sorted first. Fixed by routing
  every per-unit path through one matcher (`FindEntryForUnitDefinition`) that tries the full `pawnDescription`
  first (distinguishes `_01`/`_02`) and falls back to `coreDesc` (never regresses a working bind). Latent for the
  reference pack (no stem collisions) but a real correctness gap for third-party packs.

- **FACING SURVIVES RESPAWN (2026-08-16).** `respawnAfterLoad` units (the helicopters) lost their saved heading on
  load: the ~3-frame post-load `UpdatePawns` rebuild recomputes `FormationAngle` to neutral *after* the single-shot
  facing-restore already fired and closed (non-respawn units like the organ gun kept theirs). Fixed by coordinating
  the two systems — `MaybeRespawnPostLoad` re-arms `FacingPersist` right after each respawn, which re-applies the
  saved angle once the rebuilt unit is loaded + stationary (same frame, no neutral flash; still skips units the
  player is moving, so no crab-walk). Verified: a helicopter saved facing east holds its heading across a reload
  (`[Facing] re-applied army … after respawn`).

- **FORMATION PURE-REPOINT REFORM — critical-review follow-up (2026-08-16).** The catch-up that re-instantiates
  units which spawned *before* a formation override landed only fired for entries carrying dummy data — a
  **pure-repoint link** (points a unit at a formation already in the DB, no authored dummies) was excluded by the
  `dummies.Count > 0` gate, and its "already full?" test compared against `e.dummies.Count` (= 0), so its
  pre-override units kept the old pawn count until a reload. Fixed with a new `Entry.targetCount` (the target
  formation's real `Dummies.Length`), computed in `ApplyOne`, and by including unit links in the reform selector.
  Zero change for ENC (all its entries carry dummy data → same inject/overwrite path); protects third-party packs
  that repoint a unit to a vanilla formation.

- **GAMEBINDING COVERAGE — the army-walk root (2026-08-16).** Critical-review finding #5: the `Presentation`
  Dep was catalogued with *zero members*, so `PresentationEntityFactoryController` — the static army-walk root
  that respawn, facing-persistence, class-scan and the descriptor census all read — wasn't validated. A game
  rename there would silently no-op all four with nothing in the health report. Added it (plus the factory's
  `PresentationArmyEntities` next hop): catalog 46 → 47 types, report clean (`OK — 47 game type(s)`). The
  fragility-plan "make drift loud" template applied to exactly the code the recent respawn/facing fixes touch.

- **AUDIO DEATH/BATTLE GATE (2026-08-16).** Critical-review finding #6: the `_audioOn` poll gate omitted
  `soundDeathFile`/`soundBattleFile` while the loader right below it (and `OnPawnDeath`/`ProcessBattleCries`)
  consume them — so an entry with *only* a death rattle or *only* a battle cry never entered the poll, its clip
  never loaded (silent death cue), and `ProcessBattleCries` re-enqueued the cry every frame forever. One-line fix:
  add both fields to the gate, mirroring the loader's own check. Zero change for ENC (no death/battle entries);
  protective for its built-but-unshipped creature voices and third-party packs.

- **STATE-MACHINE GATE (2026-08-16).** Critical-review #8: `StatePose`/`ProcessAnimStates` ran only when
  `moveAnimId >= 0`, but attacks armed on `attackAnimId >= 0` — so a move-less state-driven model (idle+attack,
  no move clip) armed fires that never animated. Fixed with a shared `ModelEntry.AnyStateRole` predicate driving
  all three gates, plus a guard on the `moving` pose branch so a move-less model that moves falls back to idle.
  Zero change for ENC (all its state-driven models have a move clip); protective for a stationary-turret-style unit.

- **RUNTIME-CLONE LEAKS — critical-review #7 (2026-08-16).** Three more leaks in the district-clone family:
  (1) `InjectHandProp` overwrote `e.handPropLayer` with a fresh `Instantiate` clone on every re-inject (LOD /
  save-load / respawn drops the prop fragment), orphaning the previous native FxOutputLayer — now the old clone is
  Destroyed first (affects ENC's hand-prop units, no visible change). (2) `BuildAdjustedAtlas` and (3) `MakeGrayCopy`
  (the B&W footprint) returned `null` on a `ReadPixels`/`Apply` throw without releasing the pooled RenderTexture,
  restoring `RenderTexture.active`, or freeing the half-built texture — and `TickOne` retries every frame. Both now
  use try/finally so the RT + active are always cleaned up and the partial texture is freed on failure. Normal
  rendering unchanged; verified no-regression in-game.

- **BAKE-SCRIPT SILENT-MIS-BAKE GUARDS — critical-review Tier 3 (2026-08-16, in the ENCReload `Tools/`).** Three
  bake foot-guns that shipped a broken rig with **exit 0** are now loud aborts: (4A) `rig_anim.py` printed the
  rest-fold frame-0 residual (`should be ~0`) but never asserted it — a fold that completes yet leaves a bone
  displaced (the "head off shoulders" class) shipped silently; now aborts on NaN or a residual > 25% of the rig's
  bone scale. (2A) a failed `transform_apply(rotation+scale)` on the conversion path was swallowed with a warning,
  shipping a skeleton ~100× off the mesh; now hard-fails. (4B) `deploy_convert.py` with zero animated parts built a
  StaticRoot-only rig and shipped a static single-bone model; now aborts. Verified: the OrganGun re-baked clean
  (residual `0.000000` asserted OK, rotation+scale applied, `ANIMATED DONE`, no false abort).

- **MODEL FACTORY — Remove flow fixed (2026-08-16, ENCReload editor).** Critical-review #1 + a follow-on: (a) the
  Remove button reset `selected` but not `sel`, so the popup-apply reloaded the stale index on the shrunken list —
  jumping to a different entry, or `IndexOutOfRangeException` when the removed entry was the alphabetically last
  (Clone already reset both). (b) The "delete baked assets?" prompt was a second sequential modal that could be
  missed, and once the entry was gone it could never be re-triggered (orphan assets, no cleanup). Both replaced by
  one `DisplayDialogComplex` on Remove — **Remove + delete files / Cancel / Remove, keep files** — so the delete
  question is always asked once, reliably; deletion still uses the exact `OutputSuffixes` whitelist (never a glob).

- **BATTLE GUNNERY — the Jagdpanzer arc (2026-08-06).** A casemate tank destroyer exposed, one shot at a
  time, that vanilla **never rotates a vehicle's hull in battle** (vehicles aim only via a turret bone slot —
  invalid on custom rigs), and grew the full gunnery chain in a day: **battle hull-aim** (the map bombard's
  aim machinery armed per volley — the eased hull lays on the actual target, `hold=1` waits for the lay);
  the **gun-vs-turret model** in the Animation Lab (a Turret bone *yaws* and classifies the vehicle turreted;
  a Gun bone aims with the hull and only *elevates*); **distance-proportional gun elevation** (user spec:
  raised by range to a configurable max, rising while the hull turns, lowered after the shot); the **muzzle
  dial gone gun-local** (rotates with aim + elevation, now moves flash, tracer AND smoke — a world-space dial
  can't follow a turning hull, and a bone's TRS sits at the breech, not the barrel end); and **post-shot
  facing that settles on the nearest clean facing toward the shot** (v1's yield-on-yaw-change heuristic
  couldn't tell a real order from the choreography's own post-fight reset — graveyarded). Every asset in the
  chain is the game's own; HAF only fixes where and when. See [docs/Turn-Ease.md](docs/Turn-Ease.md).
- **CATEGORY TURN EASE — every unit type turns in character (2026-08-06).** Turn ease graduated from a
  per-model knob to a **game-wide system with per-TYPE defaults** — human / land / turret / hover / ship —
  each classified by **characteristic, never by name** (user rule, enforced twice): capability profiles, the
  game's own `Hover` "ignores terrain" ability, and live azimuth-transform detection for turrets; fixed-wing
  planes are excluded outright (they already fly natural curves — user call). Hover and ship carry their own
  bank (`hoverbank`/`shipbank`: a chopper banks, a ship heels), and precedence flipped to per-model > per-unit
  link > category > global. Getting the *strike hold* to follow the category cost four measured bugs, each a
  different naming-layer trap (an entry dead-end, artillery rendering its LIMBERED variant whose name extends
  the unit's, the servant CREW answering for its gun, and artillery main-gun pawn definitions that never pass
  the addon hook at all) — closed structurally: the slow class scan reads the rendered unit itself and is the
  classification authority, and the hold reads the eased pawn's ground-truth rate off its live turn state, so
  the visible turn and the fire hold cannot disagree by construction. Configured from the Formation Override
  window's **Turn ease defaults** panel (live dial write). See [docs/Turn-Ease.md](docs/Turn-Ease.md).
- **ATTACK TURN — the howitzer pivots, THEN fires (2026-08-05).** A map bombard used to teleport-snap the
  unit's facing and fire in the same instant; now a HAF model with a turn-ease rate **sweeps to the attack
  heading first**, and every observable of the shot — muzzle flash, shot sound, shell, impact, the model's own
  recoil clip — **waits for the barrel** and lands together at alignment. Six iterations to find the real seam,
  each killed by a measurement: the battle choreography (LookAt actions, rotation FSM) is a **no-op on the
  world map** (`StepTurning` runs 0→0 — the snap is `FlipPawnsGrid(Teleport)` stamping the GPU pawn data);
  patching the unanimated-rotation method silently did nothing because **the JIT inlines it** into its caller;
  and a one-shot delay at attack start **raced the snap** and computed zero. The fix rides HAF's own seams: the
  Comanche's ObjectSpace turn ease generalized to every entry, plus three holds keyed off the same
  remaining-turn time — the artillery controller's scheduled launch/hit delays, a deferred
  `TeleportToSimpleAttack` (the muzzle/sound carrier), and the fire clip's clock pinned until aligned.
  Turn ease also smooths ordinary move-order facing for any model with a rate. Same day, two extensions, both
  verified: **vanilla units** get the identical treatment through a Formation Lab link (per-unit rate, resolved
  to the pawn descriptor at load; a link on a Common unit covers its culture-emblematic variants — found when
  the player's ZULU siege howitzers ignored the Common link, by a one-line-per-descriptor render census), and
  **true-bearing aim** — the eased turn exposed that vanilla bombards face a HEX-QUANTIZED angle (one of six
  directions, up to 30° off); the ease target now becomes the real bearing to the target tile while the strike
  plays out, so the barrel lays exactly on the city it shells. The aim then surfaced **three more vanilla
  shortcuts** (2026-08-06, each spotted by the user frame-stepping captures, each verified fixed): the strike
  ran on TWO CLOCKS (dynamic release vs padded schedule — the bang drifted ~0.25 s from the recoil; now one
  shared release timestamp armed before the flip), the attack clip teleported in at a RANDOM PHASE while the
  shell was timed to its literal event time (now deterministic frame-0 playback), and the shell + muzzle smoke
  spawned at the PRE-PIVOT barrel — vanilla captures the muzzle at schedule time, and the pawn's invisible
  transform skeleton never turns with the eased model (now: fire-time recapture + every bone TRS aim-rotated at
  the GetBoneTRS seam while the strike is live). See [docs/Turn-Ease.md](docs/Turn-Ease.md).
- **CLIFF ANTICIPATION — climbing before the edge, not into it (2026-08-05).** Terrain hug's lead point now
  also reads the *ground* ahead: where the terrain steps up, the aircraft gains that height immediately instead
  of rising at the cell boundary, and the engine's own tile-bound altitude catches up on arrival (climb-only —
  anticipating a descent would sink toward the ridge still being crossed). Needed a physics reference and one
  correction found by reading the log rather than the screen: the first probe was a plain downward raycast and
  measured the helicopter's **own army collider**, so it compared unit heights, not terrain; it now uses
  `RaycastAll`, skips units, and takes the lowest hit. Dial: `cliff` in `haf_hugterrain.txt`.
- **TERRAIN HUG — nap-of-the-earth flight, climbing only for the city (2026-08-05).** The helicopter now
  **skims low over open ground and climbs only for built districts**, instead of cruising at skyline height
  everywhere. The engine's air altitude is already terrain-relative (it follows hills for free) but ignores
  buildings — so the model's `position.z` lift is now *subtracted* wherever no built district sits under or
  ahead of the unit, with the probe **leading** along the movement vector so it climbs before the buildings.
  Two measurements replaced two guesses: the map's **tile spacing is derived** from the median
  nearest-neighbour distance between districts (6.93 units on the test map → auto match radius 3.81 = "this
  district's own tile"; a hand-picked radius lifted the unit for every field beside the city), and districts
  are classified by their private **`constructibleDefinitionName`** rather than the always-identical
  GameObject name — which exposed that Humankind renders cultivated tiles as districts too (`Exploitation`,
  `Ruin` are flat; only `Extension_*` carries buildings). Live-tunable via `haf_hugterrain.txt`
  (drop/radius/lookahead/ease + `only`/`skip` name filters). See
  [docs/Donor-Clip-Flight.md](docs/Donor-Clip-Flight.md).
- **TURN EASE — flown turns instead of the facing snap (2026-08-04, same day as the flight milestone).** The
  engine snaps a pawn's facing instantly on a move order; the Comanche now **sweeps** to its new heading at a
  capped rate and **banks into the turn**, composed under the nose-down attitude machinery. Every angle eases
  (180s included) while teleports/battle placement snap naturally — the per-pawn state is position-matched, so
  a jumped pawn simply starts fresh at the target heading. Live-tunable in-game via `haf_turnease.txt`
  (rate/bank, ~1/s poll) — dialed to feel on the first flight. Spotted as a gap by **shakee** on the milestone
  video within minutes of posting; built and verified the same evening. Per-model Factory fields are the
  planned graduation. See [docs/Donor-Clip-Flight.md](docs/Donor-Clip-Flight.md).
- **DONOR-CLIP NATIVE FLIGHT — the donor's own animation on our rig (2026-08-04).** The Comanche now flies with
  the donor gunship's **complete original animation** — hover bob, main rotor flat on the mast, tail fan spinning
  in its own **canted** ring — driving OUR baked mesh natively (`useDonorClip`, now a Factory checkbox). Cracked
  with instruments, not guesses: a `[Rest]` skeleton dump (donor rigs keep ALL rests identity; ours carried the
  glTF -90°X on bone 0 and the facing rotation on Root — each **conjugates** every animated descendant, because
  the engine composes clips ON TOP of rests) and a `[DonorAxis]` decoder that read the donor channels straight
  from the GPU records (ch2 main = pure local-Y spin ~18°/frame; ch3 tail = pure local-X ~36°/frame). The fix is
  two-sided: the plugin **rebases the injected skeleton at registration** (ancestors → identity rests with world
  positions preserved — and it MUST run before `AnimationManager.Apply`, which snapshots BoneInfos into the GPU;
  leaf rotor bones keep their orientation), and the Vehicle Lab **authors the axle frames** (main-rotor bone
  local Y = mast, tail-fan bone local X = the canted fan axle). Five failure modes catalogued on the way
  (index-shifted channels, rolled axis, orbiting rotor, vertical loop, stale-rig rebake) — the full contract
  and catalog: [docs/Donor-Clip-Flight.md](docs/Donor-Clip-Flight.md). Plus a live `haf_rotortrim.txt` dial
  (constant BR-slot tilt, re-applied to live pawns ~1/s, no relaunch) kept inert as a finishing tool.
- **A HELICOPTER WITH ITS OWN SPINNING ROTORS — and the four-mechanism ghost hunt (2026-08-03/04).** The RAH-66
  Comanche now flies with **its own main + tail rotor spinning** (Vehicle Lab Rotor/Tail-rotor roles → continuous
  bake) instead of borrowing the donor gunship's. Getting there uncovered — and defeated — FOUR stacked mechanisms
  that together were the old "a donor's rotor can't be removed" wall: (1) gunship-class units spawn a **squadron
  of pawns** via the air hardcode (formation dummies don't cap them) → stacked copies of the model; fixed by
  keep-first-hide-rest (`hideSubPawns`); (2) our own `respawnAfterLoad` **leaked live sub-pawns** per attempt →
  off for own-rotor models; (3) one leaked pawn kept the **pre-injection donor cache** → the cached-struct repair
  (SRCFIX); (4) the last ghost — a translucent rotor that survived crushing every vertex of every ContentLayer,
  every pawn sweep, and a full renderer census — was **not geometry at all**: the donor's Mecanim-event **VFX
  billboard**, a 2D rotor sprite (the user's "it has no depth" observation cracked it), dropped by the July-era
  `silenceDonorVfx` flag. One registry flag; four hours of elimination to learn which one. The live **ghost-bisect
  tool** (file-driven in-session vertex surgery: crush/restore/census, no relaunches) ships from the hunt.
  Planned: a per-NAME VFX filter (`silenceDonorVfxNames`) to drop only the rotor sprite while keeping other donor
  effects, and a `moveTilt` nose-down attitude while moving (wired, dormant).
- **HAND PROPS — a weapon on a custom skeleton (2026-07-19).** The Combine soldier **carries a textured M60**,
  gripped correctly through idle, run, combat stance, and sustained fire. The donor (a vehicle) has no weapon
  slots, so the plugin constructs the pawn fragment itself and glues the Prop-Lab mesh to the injected skeleton's
  hand bone — with a surgical GPU-descriptor patch (the naive full rebuild scrambled other units), a per-tick
  repaint of the prop's own atlas on a private layer clone (Amplitude streams weapon textures and resets the
  material), and an always-stamped import-angle override (the baked angle field doesn't survive the mod bundle —
  the engine's `-90°X` class default silently tipped every prop until neutralized). Authoring: bake in the Prop
  Lab (now with per-prop saved recipes), pick it in the Animation Lab's **Hand prop** combobox, done.
- **STATE-DRIVEN characters — idle / run / after-move / combat stance / attack fire (2026-07-19).** The Combine
  soldier **idles standing, RUNS while moving, holds a weapon-raised COMBAT STANCE while its army is locked in a
  battle, and fires its ATTACK animation when it actually shoots** — five clips per model, switched live by the
  runtime (a ~20×/s state poll + per-pawn pose selection on the proven Pose0 slot; priority attack > move >
  after-move > combat > idle). The attack trigger is a hook on the game's own per-pawn ranged-fire sequence, so
  every battle volley animates the exact shooting pawn; an **Attack repeats** knob loops a short recoil-pop clip
  into sustained automatic fire (the soldier's 0.17s `shootAR2s` × 18 ≈ 3 s of fire, runtime-only — no re-bake).
  Configured entirely in the Animation Lab: a **State-driven** toggle with **Idle / Movement / After-movement /
  Attack / Combat-idle** clip pickers; all roles bake against **one shared skeleton** in a single Blender pass
  (every clip rebaked against the primary clip's frame-0 rest — per-role rests would displace the non-primary
  clips; single-frame stance clips are auto-padded so Unity's importer can't drop them). The bake-side war story:
  Blender's bone rename only syncs the *assigned* action's curve paths, so dormant role clips exported as frozen
  statues until the paths were patched explicitly — caught by byte-level pose-data analysis, fixed, and guarded by
  a tool-version cache-buster.
- **A HUMANOID character — a full 62-bone rigged soldier (2026-07-18/19).** The Combine soldier replaces a vehicle
  unit: right-sized, standing, head on his shoulders, **turning with its movement**, idling on his own baked clip —
  the first true *character* through the pipeline (props and machines came first). Getting him there built the
  **raw-rig conversion**: auto-rigged models whose clips *assemble the body from a scrambled rest via location keys*
  (which Amplitude, rotation-only, can never play) are now **rest-normalized and visually re-baked** at bake time —
  the assembled pose becomes the rest, the whole clip is re-derived as pure rotations (in-bake verified to ~1e-4),
  the export folds units/rotation/scale into the data, collapses no-op roots, and renames bones topologically
  (Amplitude sorts alphabetically and requires parents before children). A **litmus rig** (12-deep chain of cubes,
  `Tools/make_litmus.py`) proved the runtime renders clean rigs perfectly. Also discovered en route: the game turns
  pawns through a procedural **bone-rotation layer** — the plugin clears it only for artillery models and ignores
  vehicle donors' phantom wheel-spin slots. *(With the clean rig, the unit's fired-drone projectile also displays
  again during attacks — the fully working unit: stand, turn, idle, launch.)*
- **Animated custom models — a first, one-click.** A quadcopter drone injected onto a land unit renders full-size,
  textured, and **spins its own propellers from its own baked animation** — for any number of instances. Tick
  **Animated**, press Bake.
- **A wheeled vehicle with its OWN spinning wheels (2026-07-24).** The Ehrhardt armored car (Era5) replaces the
  Armoured Car — a purpose-made *skinned* rig whose **four wheels spin in place while it drives and are still when
  parked** (state-driven: Idle = a held frame, Movement = a spin slice). En route it pinned down a nasty engine
  trap: on the legacy path a **rotating** bone flings off in-game (idle fine, movement flings) because the
  metre→centimetre FBX export leaves a **×100 sandwich** Amplitude's TRS composition mangles — the same mechanism
  as the soldier's head. The fix is **Convert raw rig ON + Fix 100× OFF** (cancels the ×100 at export), which
  overturns the old "clean rigs skip convertRig" rule for any rig with a spinning part. It also grew a hands-free
  **Auto-ground (sit on terrain)** bake toggle — drops the tyres to the skeleton origin, self-correcting and
  **size-proof** (no manual height dial, stays grounded across Size changes). Extracted from an Unreal "Game
  Template" (Fab).
- **The rotation-only barrier is DEAD — true bone TRANSLATION plays in-game (2026-07-25/26).** Decompiling the
  runtime proved the clip format supports `RotationTranslation` (vanilla tank treads use it); the "rotation-only
  law" was our own bake's strip. The opt-in **`Keep bone translations`** flag carries authored slides through the
  bake — first verified on a sliding test bone, then shipped as **the M114 howitzer's REAL kickback**: fire, the
  tube slams back and glides home, barrel lowers, shell loads, aiming raise — the animator's complete cycle,
  finally rendered (multi-segment recoil windows with per-segment speed steps: `442..530,305..441/2`). En route,
  a decade-class root cause fell: a sentinel value placed a helper bone at 10⁹ units, collapsing bone chains via
  float32 cancellation — the origin of every NaN import warning this pipeline ever produced.
- **Moving caterpillar tracks — path-instanced rigid links (treadize, 2026-07-26).** Mark a tank's tread loop
  **C (Caterpillar)** (+ barrel **G**) in the Vehicle Lab and it becomes a real rolling track: the link pitch is
  measured off the cleats (autocorrelation), the loop path is built as the classic *belt around pulleys* from the
  wheel centers + measured band radii, the mesh is cut into **half-link cells at the cleat gaps — one bone each,
  no skin blending** — and every link rides the path with advance = exactly one link per loop (invisible restart,
  tread ≈ sprocket surface speed). Seventeen revisions of blended-skin approaches lost to the eye's verdict —
  "molded links bending = slack" — before rigid instancing won; the full post-mortem lives in
  [Animation-Pitfalls](docs/Animation-Pitfalls.md). Runs in-game (after a five-defect debugging chain); a remaining
  idle micro-twitch is the open polish item.
- **A turret that AIMS at the target (turretize, 2026-07-24).** The armored car's turret now yaws to track the
  enemy — by hijacking the game's OWN aim: the engine streams a heading angle into a `PawnEntry.BoneRotation` slot
  that lands on an invalid bone index for injected models, so we retarget that slot to the turret bone and the
  engine's aim math drives it (no per-frame trig). Runtime-only (**Turret bone** + **Turret aim axis** in the
  Animation Lab, Save + relaunch). The aim axis is per-model — yaw for a turret, and the *same* knob gives **pitch**
  for a future mechanized howitzer/artillery barrel to elevate at range.
- **Fire-on-attack — a model that animates when the unit *fires*.** Tick **Fire on attack** and the baked clip plays
  **once, on the combat action**, not on a loop: the model rests, then plays a single pass the moment the unit attacks and
  returns to rest. Proven with a **howitzer whose barrel elevates only when it bombards** — the plugin hooks Humankind's
  own combat event bus, matches the firing unit to the injected model, and triggers one playthrough.
- **First-instance rotor fix.** The engine draws the *first* borrowed-rotor pawn of a model, at the moment it's **created**,
  with its rotor ~1 unit low (a spawn race — every later instance is fine). Ticking **Re-spawn after load** makes the plugin
  watch for any such unit appearing — on a save-load, built in a city, or dev-spawned — and near-instantly re-run the game's
  own pawn rebuild (`PresentationUnit.UpdatePawns`) on it, a presentation-only refresh (no unit touched) that clears the low
  rotor. Applied to every instance as it appears (one brief flicker each) so a buggy one is never missed. Opt-in per model;
  the re-spawn delay is tunable in the plugin cfg (`Factory/RespawnDelayFrames`, default 1) for slower machines.
- **Freeze the donor's motion.** A *static* model riding an animated ground/hover donor inherits the donor's idle/move bob;
  **Freeze donor animation** pins the donor's pose so a rigid model (an airship) holds still while it still glides
  tile-to-tile — applied across *every* instance the same way animated models are (descriptor-matched + skeleton-forced).
- **Borrow the donor's animation — including *multiple* moving parts.** A model rides a donor unit's rig; injection can't
  *remove* a donor's animated sub-part (a rotor), but you can turn that into a feature: **strip your model's own rotor(s)**
  and the **donor's spinning rotor shows through**. The donor helicopter has *two* rotor bones (`Helix` main +
  `Helix_back` tail), so stripping both the Comanche's main *and* tail rotor gives it a spinning main rotor **and** a spinning
  shrouded fantail — two borrowed animations on one static model. Or give the model **its own** clip.

## Districts

- **STRATEGIC MESH FOOTPRINT + scoped-path migration (2026-08-15).** A district's zoomed-out strategic footprint is now
  its **own 3D building**, not a flat decal. The strategic fade turned out to be a **per-element GPU render-feature gate**
  (`FxEvolverMaterialLevelBuildElement.RenderFeatureSelector.SelectionFlags0`), not a camera swap — zeroing it (AlwaysEnabled)
  keeps the mesh drawing in every zoom band. On top of it: **black-and-white when zoomed out** (bind a greyscale albedo
  keyed to `RenderFeatureProvider.ComputeRenderState` of the *Topographic* band) and **flatten to a sheet** (a `size.y`
  multiplier — vertical placement is terrain-owned, so a "lift" was a proven dead end). All five settings are authored
  **per-district in the District Factory** (`footprintMesh`/BW/Flat/FlatHeight/HideDecal), falling back to the plugin
  config. Any district **migrates onto the scoped render path with one Bake** (`BakeScopedSelector` clones a
  single-building footprint template, swaps in the district's FxMesh, keeps the decals → a data-authored
  `CityMapSelector`), retiring the legacy isolate/repoint route. **Two custom districts now coexist independently**
  (breeder reactor + a Greek-temple Oracle in one game, each with its own texture + footprint) — which needed a
  per-district `ScopedState` refactor *and* moving the driving calls inside the per-district loop (they had run for the
  *last* district only). Composed **grove foliage** rendered partially (255 sub-particle cap → raise
  `DistrictMeshDensityBoost` to 32) and solid (opaque borrowed material → flip it to alpha-cutout, `_Mode=1` +
  `_ALPHATEST_ON`). Also fixed early in the session: the reactor's long-hunted "center rock" + ground twitch were
  **grafted footprint decals**, not terrain (filtered in `GraftFootprint`). Full write-up:
  [District-Dedicated-Visual.md](docs/District-Dedicated-Visual.md).

- **FOUNDATION PLINTH — planting on a cliff (2026-08-09).** A district on a coastal cliff/uneven tile
  overhung into empty air (the breeder reactor floated off the ledge). A bake-time **Foundation depth** knob
  now extrudes the building's footprint **straight down into the earth** (true world −Y, taken in drawn space
  post-rotation so it's independent of the model's import angles, then inverse-rotated back) as a solid
  concrete plinth — four walls + a floor, wound outward, cap omitted under the building. Districts render one
  atlas, so the plinth needs concrete *in* it: `AppendConcreteStrip` grows the atlas set by a fresh strip
  (noised grey albedo / neutral normal / rough concrete), slides existing content down and remaps the mesh UVs
  — **no existing texel is overwritten**. Purely bake-time: the runtime still gets one FxMesh + atlas. Two
  preview fixes rode along: re-point the preview material after the strip rewrites the atlas asset, and frame
  the camera on the **above-ground** building so plinth depth doesn't shift the view center. The health panel
  also stopped false-warning "typo" on **base-game (`Extension_*`) targets** — their definitions live in the
  game, not the project, so it stays silent (only a non-namespaced miss is a real typo). Verified in-game on
  the reactor's cliff tile. **Known limit**: a Z-fight shimmer where the plinth meets the building's own walls
  at map distance — measured to be **depth-buffer precision** (far from world origin under a huge far plane),
  not geometry; a small gap is invisible to the buffer and a large one shows a visible slot, so it's deferred
  with the shape intact (the fix path is to inset the plinth behind the building wall so a depth-beating gap
  hides).
- **HEXAGON SCULPTING — the raised platform (2026-08-09).** A district carves a raised terrain plinth
  (`UpdateHexagonSculpting` → `HexagonSculptingDefinition` → `ApplyHexagonSculptingDefinition`); a custom
  wonder's cell is empty, so the Oracle sat flat. The **fourth empty-cell fix**: a postfix forces a chosen
  index — per-entry Factory **Footprint** field + global config + a `haf_hexsculpt.txt` **live dial** (re-carve
  without relaunch, cycle ~40 shapes fast). Measured which shape to use: most districts resolve to `None`; the
  raised plinth belongs to the **emblematic quarters** (`Extension_Era1_OlmecCivilization` →
  `EmblematicAndCityCenter26`). Verified in 3D on the Oracle. Two honest limits documented: the **preview can't
  show it** (runtime terrain deformation, not baked geometry — judged in-game like PBR shading), and the raised
  platform is **not** the top-down **strategic-zoom footprint** (a separate render-mode path, still open).
- **GROUND MATERIAL — the maintained field (2026-08-08).** A district paints the terrain under it via
  `UpdateGroundMaterial` (a `(Biome × affinity)` → `GroundMaterialDefinition` resolve); a custom wonder's
  affinity has no row, so the Oracle stood on bare desert. The plugin postfixes the resolve and **forces a
  chosen ground index** — the game's own blended terrain paint, not a flat mesh. It's a **per-district** field
  in the Factory (dropdown of the game's vocabulary — grass / paved / sparse), with a global config fallback.
  Verified: `Prairie_Grassland` under the Oracle's temple and grove — the same empty-cell insight as the wonder
  visual, applied a third time, now to the terrain layer. The Factory **preview textures its tile with the real
  terrain image** — extracted from the game's shared `DefaultTextureAtlas` (resolve authoring data → atlas +
  element GUID → `GUIDToIndex` → `GetElementData` UV rect → crop the page tile → PNG per material), plus the
  material's true colour as a fallback — so the terrain-paint choice reads as real grass/pavement/sand before
  launch.
- **DE-ENC — framework filenames dropped the pack prefix (2026-08-08).** HAF is a universal framework, so its
  registry and tuning files shed the `enc_` badge of one pack: `enc_districts.json` → `haf_districts.json`,
  and likewise `haf_models` / `haf_formations` / `haf_sounds` / `haf_props` and the live-tuning `haf_*.txt`
  dials (177 references across 35 files, both repos + the git-tracked backups + the deployed data). The pack
  itself keeps its identity — `haf_packs/ENCReload/`, `pack.json`, its own skins/sounds — because that name is
  the pack, not a prefix.
- **DISTRICTS GO MULTI-INSTANCE (2026-08-08, verified with a second reactor).** A critical review of the
  district axis found its one architectural flaw: each registry entry held ONE component slot, overwritten by
  whichever district instance last refreshed — build the same district in two cities and ownership ping-ponged,
  only one tile showing the custom model. Fixed by splitting targeting from assets: each entry now tracks a
  **list of live instances** (added per `UpdateLevelBuild`, pruned via fake-null when razed) while the private
  leaf, layer clone, and texture bindings stay **one per entry, shared by every tile** — a leaf is just a
  material, and vanilla's shared selectors serve many channels the same way. The same review also flattened the
  per-frame hot path (cached reflection handles, cached texture bind slots — twenty districts now cost what two
  used to) and collapsed a drifted hand-rolled copy of the session reset into the canonical one.
- **SURFACE MAPS GO PER-ENTRY — the reactor regression (2026-08-08, same day).** The stability pass had bound
  flat neutral surface maps on *every* custom district; the temple then earned real baked maps, but the Breeder
  Reactor silently kept the neutrals — which turned its verified look (albedo over the donor silo's vanilla
  maps) into chrome domes and near-black walls, unnoticed for two days until its city was next visited. Fix,
  verified on both districts: entries with baked normal/rough atlases bind them; entries without keep the donor
  material's own maps. Lesson: a shared-code change verified on one district is not verified on the axis.
- **THE REVEAL-RAMP LEVER — wonders load complete (2026-08-08, same day).** Every session load replayed the
  bottom-to-roof level-build reveal on the custom wonder — vanilla plays the same ramp, the loading screen just
  hides it, and our swap necessarily lands after the screen lifts. Racing the loading screen was **falsified
  twice** (silent deadlocks — reaching for the render context from a plugin Update tick during the load
  sequence hangs the game with sync AND async loaders; LAW: never before `distFxManager` is tracked). The
  answer was a field dump away: `FxEvolverMaterialLevelBuildElement.fadeInOutMode {Stepped, Smooth, Instant}`,
  the appearance transition itself, encoded per element into GPU data. The wonder-path private clone sets
  **`Instant`** before its first Load — the temple stands complete the moment the tile renders. Open refinement:
  an `UpdateLevelBuild` event capture to keep the `Stepped` ceremony for wonders genuinely completed mid-game.
- **NATIVE WONDER VISUALS — the empty-cell revelation (2026-08-08).** One day after shipping, the Oracle's
  donor-district hack died of obsolescence. Three donor swaps failed in a row (Holy Site: bare tile; Natural
  Reserve: swap landed but drew nothing visible), so instead of donor roulette the visual-resolution chain got
  decompiled — and the "mod can't extend this" verdict of July collapsed: district visuals resolve through
  **criteria-matrix databases** whose rows are **plain datatable elements**, and completed wonders key their
  model **by wonder name** in a dedicated `ArtificialWonder` database. A `[RepoDump]` launch delivered the
  punchline — *our wonder's name was already indexed there, with a NULL guid*. July's `material 0,0,0,0` was
  never a dead end, just an **empty cell waiting to be filled**. Now `[WonderRow]` fills it (Temple of Artemis
  material as zero-bake proof + loaded template), the walker sources its swap template from the cell, and the
  proven isolate machinery does the rest: the Oracle renders its custom temple through the **game's own wonder
  pipeline** — native affinity, no donor anywhere, and the vanilla **bottom-to-roof level-build reveal** plays
  on the custom mesh after a reload. Donor laws measured en route (building-model + culture-agnostic families
  only; scatter families draw wrong; repository-fed families have no inline leaves) are kept in
  [docs/Wonder-Spike.md](docs/Wonder-Spike.md) as history.
- **THE ORACLE — first custom Artificial Wonder, shipped & announced (2026-08-07).** A Sketchfab Greek temple
  became a fully playable custom wonder in one arc: the district swap machinery carries `ArtificialWonderDefinition`
  unchanged (donor = a renderable district affinity; the *designed-for* native wonder affinity was measured a
  dead end — scaffolding-only material family, zero swappable leaves). Stability took a same-day triad, each
  mechanism measured: **streaming opt-out** (the private layer clone nulls its mid/hi-res material GUIDs so the
  reduction system can't stomp the injected albedo), **neutral surface maps** (the donor's bricks no longer bleed
  through), and a **session reset from the `Sandbox.Load` postfix** (save-reload had been re-pointing onto a
  corpse leaf → empty tile). Then the temple got its marble: **normal + roughness atlases baked with the albedo
  pack's exact rects** (the walls' albedo is pure white — the beauty was in the surface maps all along), area-average
  downsampling (a single bilinear tap aliases dense normals into rainbow static), relief calibrated into the data
  so preview and game agree. Card/small/tooltip portraits ride the standard UIMapper `Images` slots. Announced on
  Discord the same evening. See [docs/Wonder-Spike.md](docs/Wonder-Spike.md).
- **DISTRICT TEXTURES — the nuclear plant arc (2026-08-06).** Replacing the Breeder Reactor's model (a
  Sketchfab site-plan plant) turned one swap into three capabilities. (1) The **District Factory grew an
  embedded preview pane** — the baked mesh, textured, on a tile-sized ground square at the true in-game
  surface level, import angles live — after its first version *hid* a grounding bug by anchoring the ground
  to the model's own bottom. (2) That bug (the plant surfaced only its containment domes) exposed that the
  game plants the mesh by its origin and nothing re-grounds a rotated bake — the district bake now
  **auto-levels**: vertices shifted so the model lands lowest-point-on-the-surface *with its import angles
  applied*, any rotation combination stands level. (3) **Districts finally wear their own texture.** Three
  weeks of flat-shaded custom districts ended with two measurements: the district building layer is a
  **full-texture layer** (no atlas manager — leaves sample the layer material's bound sheet through mesh UVs,
  which is why an unbound custom mesh wore *patches of the culture's building sheet*), so texture is a
  per-layer binding — and `FxComponentRenderer.GetLayerIndexAddItIFN` **registers any output layer handed to
  it**. So the private leaf now brings a **private clone of the whole FxOutputLayer**: the game registers and
  loads it itself during the leaf's own Load, and the plugin binds the baked albedo on the clone's runtime
  materials. One tile, exact UVs, zero effect on every other building. A rect-painting design targeting the
  atlas manager was built first, falsified by the trace, and never shipped. See
  [docs/District-Visuals.md](docs/District-Visuals.md).

## Authoring tools

- **THE PIZZA BAKERY — multi-model districts (2026-08-08, verified: the Oracle's temple + a beech tree).** The
  District Factory composes MULTIPLE models onto one tile: parts bake with their own knobs, auto-ground to the
  base's floor, and merge into one mesh with super albedo/normal/rough atlases sharing one rect set — the
  runtime never learns the word "pizza" (one FxMesh + atlas trio per entry, so isolation/wonders/multi-instance
  compose for free). The dressing fought back three times, each measured: the multi-material pack
  **force-flattened alpha** (a=255) and the atlas compressed to **DXT1 (no alpha channel)** — cutout foliage
  baked as solid triangles until both were made alpha-aware; the v1 albedo-only compose dropped the temple's
  surface maps and the donor's maps **turned the marble blue** — super normal/rough maps with same-rect
  area-average blits brought it back; and the game's **shadow pass doesn't alpha-test**, so a dense leaf crown
  casts a soft solid blob (cosmetic, documented). The headline discovery: **the district shader honors
  alpha cutout** — card-foliage trees are first-class district dressing.
- **DISTRICT FACTORY HEALTH PANEL (2026-08-08, verified through its full lifecycle).** The review's last
  finding: the week's two costly failures — registry-vs-asset GUID drift and the stale mod bundle — plus
  July's data-prerequisite trap were all detectable at authoring time, and now they are. On selection, after
  every Bake, and on Re-check, the window compares every shipped GUID against the asset on disk (mismatch =
  red box instead of a silent "waiting for leaves" launch), the newest baked asset against the newest built
  Community assetbundle (bake → STALE BUNDLE warning → rebuild → clears), and the district definition's data
  (non-empty Additional Visual Levels = the guaranteed-empty-tile error; missing affinity = warning). One
  green line when everything agrees.
- **DISTRICT COMPASS ROSE + CORNER-FORWARD HEX (2026-08-08, verified in-game).** The district preview's tile
  hex was drawn edge-forward — the *unit* convention — but the in-game district cell presents a **corner**
  toward the model's forward (user-measured on the reactor). The shared hex builder gained an orientation
  parameter (units 30°, districts 0°) and the bake's hex-clip planes rotated to match the real cell walls. The
  facing arrow became a **NESW compass rose** — lines to all four cardinals, letters reading North-up — since
  a district has no facing of its own: what its author needs is map orientation, and the preview and the game
  now agree on it.
- **THE PREVIEW TRUTH ARC (2026-08-07).** Two days that turned the editor previews from bake-inspection aids
  into placement instruments a pack author can trust. The shared conventions, across the Model Factory,
  Animation Lab and District Factory panes: a **true-size tile hex** (6.93 across flats — the measured
  center-to-center tile spacing; the old ~10 square flattered every fit) pinned at the **origin plane** (never
  anchored to the model's bounds — that once hid a half-sunk district bake); **water-blue for boats** (the
  pawn's own Boat capability profile, never the name); a **forward arrow** (+Z, verified against the
  Jagdpanzer's barrel; edge-on, the six hex facings); **Center** re-frame and 2× deeper zoom. The Factory and
  the Lab now share the faithful **rest-pose FBX view** for animated entries — attempt 1 force-reimported the
  shared FBX and scrambled tiling-UV preview textures (reverted, root-caused, re-attempted read-only:
  VERIFIED). The arc's crown was the stealth helicopter's centering: the hex made a years-invisible off-center
  bake obvious, which exposed that **Position offset was silently dead on the animated path** — now applied in
  the rig conversion in true **game units** (pre-divided by the FBX import's `size/longest` factor that used
  to multiply the dial ~3×) — and that **donor-clip models are re-anchored by the donor rebase** (in-game
  position ≠ FBX position; three launches burned misreading placement over a district tile). Previews now show
  donor-clip entries **footprint-centered** — the measured approximation, ±0.5 units on the helicopter, honest
  caption included; exact rebase-in-editor prediction is the documented open end. See
  [docs/Donor-Clip-Flight.md](docs/Donor-Clip-Flight.md) and [docs/Editor-Tools.md](docs/Editor-Tools.md).
  **CORRECTION (same day, user-caught):** the animated-path offset bake and the donor-clip approximations were
  built on a **doubled signal** — the plugin had applied the registry `position` at RUNTIME all along
  (`ApplyPositionOffset`, per frame, pawn frame, game units), so baking it too made every animated model carry
  the offset twice: the helicopter flew at *exactly 2×* its dialed height, and the "calibration" launches were
  fitting multipliers to runtime + bake in two different frames. The user's arithmetic (halving the dial restored
  the exact old height) exposed it. Unwound: bake-side application removed, footprint-centering removed, previews
  draw the **runtime offset live** — one dial, one application, no re-bake to nudge a model. The lasting morals:
  *grep for a runtime consumer before resurrecting a "dead" knob*, and *a knob that seems to need calibration
  usually has two writers*.
- **Vehicle Lab: helicopters + interior-part detection (2026-08-03).** Two new part roles rig a **rotorcraft** the
  same no-Blender way as a wheeled vehicle: **Rotor** (`R`) and **Tail rotor** (`L`). Each rotor fuses into **one
  hub bone** (proximity clustering would shred a wide blade disc into pinwheeling halves — the RAH-66's 18-unit
  disc proved it): the main rotor pivots on its central hub part and spins about that hub's own *pole-to-pole*
  axis, the tail fan pivots on its blades' centroid and spins about the axis *perpendicular to the duct ring*,
  with an own Auto/X/Y/Z override + **yaw/pitch trim sliders** for the last degrees by eye. Rotors are exempt from
  the wheels' rolling-contact speed scaling (it span the small tail fan ~3.6× too fast), Verify understands them
  (1 hub per group; car-only symmetry checks skipped), and new preview aids — **Pause**, **one-frame step ◀/▶**,
  a **Level line** at rotor height — make the axle judgeable. Preview-verified on the RAH-66; in-game bake pending.
  Same day, the probe gained **escape-ray visibility classification**: every part is tested for a straight
  line-of-sight to infinity, and a **Visibility switch** (All / External / **Interior only**) surfaces the parts
  that are *provably never visible* — cockpit gear, engine guts — for a one-key **Ignore** sweep. On the RAH-66 it
  found 47 interior parts worth **28% of the model's vertices** (11,042 → 8,651 in the generated rig), budget
  returned to the shared GPU vertex pool. Deliberately conservative: a part that peeks through any opening counts
  as external.
- **The Vehicle Lab — any static vehicle model becomes that unit, no Blender knowledge (2026-07-25).** A dedicated
  window "vehicleizes" a raw model: headless-probe its parts (a 3,350-shard game rip included), mark wheels &
  turret with a keyboard-driven review UI (zoom-highlight preview, classification filters, height-slab sliders,
  save/load **recipes**, a clustering-accurate **Verify** report), and it builds the rigged, LINEAR-`Spin` GLB the
  animated path consumes — wheel shards **clustered per hub** so spokes revolve around the axle, one mesh per bone,
  the rip's stowaway skeleton stripped. **Verified in-game the same day: the shipped ArmouredCar now runs a
  Lab-generated rig** — grounded, turret aiming (axis Y on generated rigs), muzzle flash re-anchored on the
  `Turret` bone. Rips that ship **already rigged** (`SKM_`) get a **fast path**: the probe detects the skinned
  artist skeleton and the Lab marks *bones* instead of shards — Spin authored straight onto the source rig,
  weapon/socket bones preserved (it inherits the artist's weighting; the shard flow stays the quality reference).
- **The Animation Lab — animation authoring in its own dialog (2026-07-18).** `Tools ▸ HAF ▸ Animation Lab` docks as
  a tab beside the Factory: the Factory owns the *model* (file, transform, size, shading), the Lab owns the
  *animation* (clip + bone-filter pickers, fire-on-attack, deploy-on-stop + recoil, and **Save (no bake)** for
  runtime flags). Settings are mutually exclusive between the windows and **enforced at bake time** — each window
  rebases on the freshest registry entry and writes only its own fields, so stale copies can't clobber each other.
  Geometry re-processing is **automatic** (the Blender step re-runs exactly when one of its inputs changed); the old
  "Reuse extracted" checkbox is now purely **"Keep extracted texture (hand-edits)"**.

## Textures & meshes

- **Multiple static models live**, no new code each: a **Zeppelin**, an **LCAC Hovercraft**, a fully-textured **USS
  Zumwalt stealth cruiser**, and a **RAH-66 Comanche** helicopter — correct orientation, correct skin, at the waterline.
- **Heavy / single-sided / multi-material meshes, handled** — a built-in vertex reducer, a winding fix + double-sided
  fallback for CAD "sketch" meshes, height-based UVs, and an N-material atlas packer. Formats: GLB / glTF / OBJ / FBX /
  `.blend`.
- **Correct, isolated textures.** Custom skins map right-side-up out of the box (the glTF-V-top vs OBJ/Unity-V-bottom
  convention is reconciled during OBJ import, and off-tile UVs — a skin mapped into the V 1→2 tile relying on wrap — are
  shifted back into range so they don't collapse to a flat smear), and each model gets a private `FxOutputLayer` so its
  skin never bleeds onto the donor.
- **Tune the skin, shrink the bundle.** Bake-time **Albedo brightness / saturation** lift a dark or washed-out skin (the
  injection ships *flat* albedo — donor PBR neutralized — so a shiny/dark source reads muddy without this); a **Keep black**
  toggle preserves an intentionally black material (a glass canopy); and **Atlas size** (256–2048, default 512) + DXT1
  compression keep each shipped skin ~0.1–2 MB. Bake *inputs* live in `Assets/FactorySource/` — out of the shipped mod, so
  the licensed source models are never redistributed.
- **Strip parts of your model at bake time.** A "Strip parts" field deletes named objects (+ children) from the source
  mesh before baking — the mirror of Hide-donor, on *your* model. Drop a helicopter's own rotor, a crew figure, a weapon
  pod… Name-Pick reads objects straight from the GLB/glTF. Proven removing the Comanche's rotor blades.
- **Retexture / recolour without a bake.** A separate **Unit Retexture** window reskins an existing unit at runtime —
  a hot-loaded PNG or a live Desaturate + RGB adjust on its own atlas — isolated per unit, free on the vertex budget.
  Works on **baked custom models** too (the PNG replaces the baked atlas — recolour without a re-bake), with a live
  in-editor preview of the exact skin that will be injected.

## Audio

- **Unit movement audio — engine sounds & custom WAVs.** Injected/retextured units are silent on move (the game's per-ship
  engine sound rides an audio-service path our re-loaded units never fire). The plugin restores it — playing the game's own
  sound **by name** (works from the *first* unit, no capture; F8 **Dump Sound Catalog** lists all ~845 event names) — or
  **any custom WAV you drop in**, as a **Start (spool-up) → Travel (loop) → Stop (spool-down)** sequence with per-clip
  volume, driven by the dedicated **Sound Studio** editor window (with in-editor ▶ preview). Runtime-only, no bake.
- **Creature voices — silence the donor, add your own growl and attack roar.** A borrowed animal donor drags its Wwise
  voice along (the Abomination's bear donor growled and mauled through every re-skin); `silenceDonorAudio` drops it at
  runtime. In its place: an **Idle growl** WAV on a jittered interval with a **one-voice radius** (a 5-stack snarls one
  pawn at a time, not in unison), and an **Attack sound** fired at attack *commit* — camera-anchored so it stays audible
  at battle zoom, with a **start offset** that skips a WAV's silent windup so the impact lands on the swing. A **Death
  sound** (rattle/scream as a pawn falls) and a **Battle-start war cry** (once, the moment a battle begins with the unit
  in it) complete the arc: alive → to arms → fighting → gone. (Growl + attack verified in-game 2026-07-23; death + war
  cry built, in-game verification pending.)

## Multi-mod & safety

- **Multi-mod — merge packs from many authors (2026-07-19).** The runtime is a **Humankind Asset Framework** host, not just
  ENC's loader: it merges ENC's base registry with any number of third-party **packs** dropped in `BepInEx/config/haf_packs/`,
  so a modder augments their own units with a custom model / texture / sound by shipping just a config file + assets — **no
  ENC edits, no code**. Pack resolution is **enforced**: duplicate `modId`s rejected, `dependsOn` validated, load order
  topologically sorted over `dependsOn`/`loadAfter` (cycles broken loudly), **declared `overrides` replace** the targeted
  entry, and an undeclared same-pawn clash stays first-loaded-wins, logged loud — no silent overrides. Every load writes a
  `haf_load_report.txt` with the resolution decisions.
- **Backup & Restore — a safety net for the un-versioned assets.** ENCReload's git tracks only `Assets/Databases`;
  a **Backup and Restore** editor window snapshots everything else (editor tooling, source & baked models, databases,
  `Tools/`, and the live BepInEx runtime config) to a timestamped, manifest-backed folder on `D:`. Restore is guarded —
  it auto-snapshots the current state first, copies back **additively** (never deletes work you've added since), and
  verifies file counts.
