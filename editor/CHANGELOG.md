# Changelog — HAF Authoring Tools

The **package** changelog: what changed for someone who installs the tools. (The project-wide engineering log
lives in the repository's root `CHANGELOG.md`.) Versions are also git tags: `editor-vX.Y.Z`.

## 0.5.7 — unreleased

- **ONE axis frame — the static bake now matches the animated one, no toggle, no legacy mode.** The static
  path used to ingest a GLB's Y-up vertices raw into the Z-up baker world and then auto-align the longest
  axis by guess — so the same file needed a different Rotation per path (the Lembos arrived upside-down on
  one and level on the other). A first attempt shipped this as a per-entry "Unified axis (v2)" toggle with a
  byte-identical legacy mode; the TOW acceptance test sank it (the flag didn't reach the bake, and a rigged
  Vehicle-Lab GLB extracted unassembled either way) and the ruling was one convention, period. Now: glbconv
  ALWAYS converts Y-up→Z-up (+90°X, normals included, winding preserved) **and evaluates skinned sources at
  their bind pose** (joint × inverse-bind per vertex — a Vehicle-Lab rig extracts assembled and upright, the
  TOW tripod under its launcher instead of scattered), the longest-axis auto-align is gone, and Rotation is
  the only orientation knob with the same meaning on BOTH paths — including at nonzero values: the static
  combine applies the registry fields exactly the way the animated path does (X = pitch, Y = heading/yaw,
  Z = roll, composed roll→pitch→yaw; review P1 — a plain Euler on the Z-up frame would have made Y a roll).
  The converter reads every JOINTS_n/WEIGHTS_n influence set and judges mirrored winding per vertex from the
  actual deforming transform (review P2/P3). Every cached extraction re-runs (cache stamp `v3`). **Breaking on
  purpose:** pre-existing static entries re-bake into the unified frame — re-dial their Rotation once
  (typically back to 0,0,0). Direct **.fbx/.obj** static sources (which never pass through the converter) get
  the identical Y-up→Z-up conversion applied in the combine instead, so the unified frame and the Rotation
  semantics hold for every source format. Field-verified on the TOW with cross-bakes at (0,0,0), (0,±45,0)
  and on the X and Z axes — every rotation field now faces static and animated identically (the last fix was
  a mirror conjugation: the static rotation applies after the importer's handedness flip, so yaw and roll
  negate internally; pitch, about the mirror axis, doesn't).

- **Flag fold — a Flag-marked part can FOLD at its top hinge instead of the naval strike.** Marking a land
  unit's stand/tripod **Flag** used to give it the ships' behavior only: mirrored below the keel while moving
  (the disappear-under-terrain strike). A new **Fold mode** checkbox in the Deploy section (with a fold angle
  −175..175° and a frames slider) re-homes the Flag bone's hinge to the TOP of the flag geometry and authors
  the `Furl` clip as a played fold — frame 0 deployed → frame N folded — while `Spin` holds the folded pose.
  Assign Pre-move `Furl[0..N]` / After-move `Furl[N..0]` and the unit folds before moving and redeploys on
  arrival, waiting for the fold like the howitzer's trails. Angle 0 in fold mode = the part simply stays
  deployed while moving; unchecked = the naval strike, byte-identical for every existing ship. Not available
  on the source-skeleton fast path (needs the generated Flag bone + Furl clip).

- **Idle/reference is `Furl[0..0]` on every flag/sail rig — and Auto-detect knows it.** The reference clip's
  frame 0 becomes the model's REST pose on the convert path, and a flag rig's `Spin` holds its strike/fold on
  EVERY frame (deliberately — movement must never flash the deployed pose mid-loop). Referencing `Spin[0..0]`
  therefore baked the hidden pose into the rest skeleton, and Auto-ground lifted the model by the struck
  part's depth (the sky-floating TOW). `Furl` frame 0 is always the deployed state, so it is the reference on
  any rig that has it: Auto-detect now recognizes a FLAG/SAIL rig and fills `Furl[0..0]` itself, and every Lab
  printout and HelpBox teaches the same. Wheel/rotor rigs (no Furl clip) keep `Spin[0..0]`, unchanged.

- **A material literally named 'Material' no longer scrambles the animated atlas.** The animated path pairs
  each submesh with its atlas cell by simplified material name — and simplifying strips the word "material",
  so a bare 'Material' became an empty string, which the substring fallback matched to the FIRST cell (every
  string contains ""). The SteamTransports' hull baked wearing the sails' canvas (white streaks) while its
  static bake — which pairs by object identity, not names — was perfect. Empty simplified names now skip name
  matching entirely and use the order-correct index fallback. Also from the same import: "Make static" no
  longer writes `deploySpeed`/`recoilSpeed` = 0 (the schema default is 1; the zeros produced two harmless but
  permanent per-bake validator warnings), and the registry floors both on every save, healing scarred entries.

- **The preview dropdown can show the Idle stance override.** "Why doesn't it hide the sails at idle?" — it
  did, in game; the Lab just couldn't show it: the stance override bakes to its own `anim_idle/` folder and
  the dropdown had no entry for it, so the only idle-looking view was the reference (deliberately DEPLOYED on
  a flag/sail rig). "Idle stance (override)" is now in the dropdown whenever that bake exists — the
  furled-at-anchor look is verifiable without launching the game. Auto-detect also stopped wiping a
  configured Furl idle stance: it can't guess one (a ship wants `Furl[N..N]`, a land flag wants it empty —
  identical rigs), but it now KEEPS what you dialed and says so in the status line.

- **Small fixes.** The Factory preview no longer shows a leftover animated rig after a static re-bake of a
  formerly animated entry (it fell through to the fresh static model only when the stale FBX was gone). The
  Animation section's probe re-runs when the model FILE changes in place, not only when its path changes (a
  regenerated GLB at the same path used to keep the section stale). "Make static" no longer scars the entry
  with `attackRepeats: 0` — the registry floors the value at 1 on every save, healing existing entries.

- **Per-source Brightness dials — merged models read as one unit.** The TOW launcher arrived several stops
  lighter than its tripod ("acts more like a whole unit rather than a patched model"): two sliders in the
  Second-model section (first model / second model, 0.25–2.5, default 1 = untouched) multiply each source's
  ALBEDO — base-color textures' pixels and textureless base colors, never normal/roughness maps — baked into
  the generated GLB in Blender, so the part preview, the probe and the Factory atlas all show the same tone.
  The first-model dial works with or without a merge. Drilled to the decimal: value materials measure ×0.600
  exactly at 0.6, and a 0.5-gray texture at ×1.6 measures 0.804 through the full export round trip.

- **The discard warning now knows whether you saved.** The Vehicle Lab's "discard the current session?" dialog
  fired on every switch, even straight after a Save. Dirty now means "differs from the last Save/Load": the
  window state is serialized and compared byte-identically against a clean-state snapshot taken at that
  moment (after the load guards run — see the review-hardening entry). A cleanly saved session switches
  silently; real unsaved changes say so explicitly.

- **Review hardening for the merge** (own 8-angle review + a convergent external one): the single-mesh
  loose split is now PER SOURCE (a combined-mesh first model kept splitting after a second model was set —
  both reviews' top finding); a typo'd second-model path fails loudly instead of crashing silently and
  wiping the probe's markings; the flatten's Icosphere purge also catches `B_`-renamed skinned bone-shape
  spheres from the second model; the discard dialog compares against a clean-state snapshot taken after the
  load guards run, so a guard-tripping recipe file no longer reads as eternally dirty; and geometry-producing
  modifiers (Array/Mirror/Solidify — `.blend` sources) are now BAKED after import, so they reach the part
  list, previews and output for the first time (they never did — the exporters always ignored them). The
  Icosphere artifact purge went from name-only to SIGNATURE + role protection (a real ball that kept the
  default name lists and survives; a marked part is never purged), and curve/surface/text objects are
  CONVERTED to meshes at import instead of being swept as helpers — they become ordinary markable parts
  (face-less wire paths are dropped; they render as nothing anywhere).

- **Two models can merge into one rig.** A collapsible "Second model" section (optional — most vehicles never
  need it) imports a second source into the same scene before the probe: its parts arrive with a `B_` prefix
  and take roles, reduce dials and rigging like any others. Offset / Rotation / uniform Scale place it against
  the first model (the scale dial reconciles cm-vs-m sources), and the Generate log prints the placed `B bbox`
  so alignment is dialed with numbers. The merge rides a tagged `merge2=` argument that both probe and rig
  modes scan, `.blend` seconds are rejected (opening one replaces the scene), and the fast path rejects the
  merge loudly. Drilled headless: Khalandion + half-scale Triconter at (0,40,0)/90° — 92 `B_` parts probed,
  bbox lands exactly at the dialed transform, full Generate exports the combined 549k-vert scene.

- **Sails can FOLD at idle instead of hiding.** A "Fold sail at idle" checkbox (shown when sails are marked)
  swaps the Furl stance's below-the-keel strike for the vanilla ships' brailed-up look: the canvas gathers
  into a visible bundle at the yard — an accordion pleat on a generated Sail→SailF1→SailF2 fold chain, the
  cloth band-skinned in height thirds and the fold bones zigzagged ±160° about the yard axis. Pure rotation
  by design (per-bone scale is the pipeline's measured AW101 trap), so the bake recipe and stance assignment
  are unchanged. Rigging keeps standing on the root Sail bone. Rejected loudly on the source-skeleton fast
  path. Drilled headless on the Khalandion: folded canvas = 0.38× raised height, top pinned at the yard.
  Follow-up ("moving too fast, in a single frame"): **Fold frames** (default 12) and **Fold angle** (default
  160°) dials — with frames the fold is the split-trail deployment mechanic on canvas: Pre-move `Furl[N..0]`
  lets the sail out as the ship gets under way, After-move `Furl[0..N]` gathers it on arrival, `Furl[N..N]`
  holds the idle stance; the legacy strike keeps its out-of-sight 1-frame snap. Two field passes reshaped the
  fold itself: first the chain grew to four bands (Sail→SailF1..F3) because parity decides where the bottom
  edge lands ("the underside should fold to the top of the beam"); then the alternating pleat became a CURL —
  "fold more how an open hand thumb and fingers close" — every joint bending the same way, the Curl dial the
  TOTAL roll (default 270° = 90° per joint). Measured at 270°: the canvas's foot lands 0.06 sail-heights ABOVE
  the beam, wrapped onto the yard like fingers on a palm's edge; bundle 0.39× raised, mid-frame 0.50 (the
  close plays through). A **Reverse curl direction** checkbox mirrors the roll to the other side of the sail
  plane — which way is "backwards" depends on the source model's facing, so it can't be auto-derived; the
  reverse roll curls against the billow camber and bundles a little looser (measured 0.53× vs 0.39×), foot
  at the beam either way. A **Sag (gravity)** dial (0–1) DRAPES the folded roll so it stops "behaving like
  in space" — first modeled as extra curl (field verdict: "it's just curling more"), now real drape math:
  gravity pulls every cloth segment toward hanging vertical, so the segment angles' horizontal component is
  squashed by (1−sag)² and the per-joint deltas re-derived (all folds share the yard axis, angles compose as
  scalars). Measured at curl 180: horizontal spread 3.89 → 2.60 → 2.05 across sag 0/0.5/1 — the floor is the
  canvas's own billow camber (rotations can't flatten a curved sheet), so sag 0.5 roughly halves the
  protruding roll and 1.0 hangs it flat; the foot stays at the beam throughout.

- **The Era Lab grid can be switched off without losing it.** A new "Apply era ageing" checkbox saves as
  `eraGridEnabled` in the pack: unchecked, the grid stays authored (and editable) but the runtime treats
  every cell as 1.0 — units keep their Resize Lab scale in every era. Absent key = enabled, so packs saved
  before the toggle are unchanged. F8's resize overlay says when a grid is authored-but-disabled instead of
  reporting "0 rows" as if none existed, and the load report names a disabled pack's grid.

- **The Animation Lab jump unlocks only after Save or Bake.** The Factory's "Open / Edit in Animation Lab"
  buttons handed the Lab a form the registry had never seen — the Lab (which edits the SAVED entry) then
  opened on nothing or a stale namesake, and the two windows fought over who defines the entry, whoever saved
  last clobbering the other. The buttons now grey out until the entry exists in the registry and the form
  matches it, with the reason in the tooltip and the detection notice.

- **Rigging rides the Sail bone.** When sails are marked, Rigging-marked parts (halyards, sheets, stays) weld
  to the Sail bone — struck below the keel WITH the canvas at idle, raised underway — but stay **single-sided**
  in their own mesh (never doubled with `Mesh_Sail`). Without marked sails, rigging welds to the hull as
  before.
- **Blade roll is mirror-correct across the banks.** Two rounds: the roll axis came from the raw PC1
  (arbitrary sign), so one dial could roll opposite banks' blade faces opposite ways (measured on the
  Triconter: sheet tilt 89° starboard vs 49° port). The first fix outboard-canonicalized the axis alone —
  measured still asymmetric, because the mirror of "roll +θ about an axis" is "roll **−θ** about the
  mirrored axis" (rotation conjugation). Final rule: outboard-canonical axis AND the angle carries the side
  sign, making the banks true mirror images.
- **A Generate can be pure geometry surgery.** The gate required a spinner, oars, or wave rock — a static
  ship needing only Flip, a facing fix or reduction couldn't Generate at all. Geometry work now satisfies the
  gate on the mesh path (drilled: a motion-less rig exports cleanly, `Spin 0..50 0 deg`); the fast path is
  unchanged (geometry work is inert there and says so).

- **Both oar banks finally mirror.** The rowing stroke's dip/lift axis derived from each oar's raw PC1
  direction — whose sign is arbitrary — and the outboard normalization ran *after* the axis was taken. A bank
  whose directions converged inboard (the Triconter's port side) got its lift inverted: blades riding a full
  2× lift below the other bank, pointing at the seabed, while the sweep stayed correct. The outboard fix now
  runs first, so the dip axis mirrors per bank by construction. (The Khalandion was unaffected — its raw
  directions happened to converge outboard on both banks, measured.)
- **Flip — a winding-surgery role** (hotkey `F`; Flag stays dropdown-only). The marked part's winding is
  reversed **once** at export, applied on top of the inside-out fix as an XOR **per island**: islands the fix
  flipped wrongly (a curved deck, a stern overhang — surfaces the belly-radial test mis-judges) land back on
  their authored winding, and with the fix off (or on islands the fix left alone) the mark alone reverses
  them. A part whose islands got *mixed* fix verdicts can't be fully repaired by Flip — split it in the
  Workshop first, or use Rudder (double-sided) there. Own **Flip reduce (%)** dial; mesh rig only (the
  source-skeleton fast path refuses it loudly, and the whole Vertices-control section now says when the fast
  path makes it inert).
- **Preserve gains an opt-in reduce dial.** **Preserve reduce (%)**, default 0 — which keeps the role's
  original byte-identical promise (and is what every older recipe loads as). A non-zero dial opts that one
  mutation in; the other exemptions (no winding fix, no doubling) remain unconditional. The Generate log
  states which mode ran.
- **Detail — a plain reduction tier of its own.** **Detail reduce (%)** for ornament/trim geometry that wants
  a dial between Structure and Body; welds to the hull like Body, no exemptions.

## 0.5.6 — 2026-09-07

- **External review hardening (three finds on this release's own fixes).** A kept failed-restore backup is no
  longer destroyed by simply retrying the bake — each bake attempt backs up into its own unique directory, so
  an unresolved recovery backup survives until you delete it. The Workshop's output path now *tracks* the
  source file while auto-derived (typing a source no longer freezes the output at the first keystroke's
  fragment); editing the field takes ownership. And the Factory's Browse unit-scale guess disarms the moment a
  save or bake persists it, closing the window where a stale guess could overwrite a newer Lab-saved value.
- **Recipe loading: absent-key defaults have one source of truth.** The load path carried hand-written
  fallbacks for keys missing from old recipes, justified by a comment claiming JsonUtility ignores field
  initializers — measured false (Unity 2021.3.1f1 batch probe): initializers DO run, and absent keys keep
  them. The duplicated fallback constants (a silent-divergence hazard) are gone; the DTO initializers alone
  define what an old recipe loads as. No recipe loads differently — every removed fallback equaled its
  initializer.
- **The source-skeleton fast path says NO instead of silently doing nothing.** The fast path spins **Wheel**
  bones only — but the Generate gate accepted Rotor / Tail rotor markings and Wave rock on it, the script
  parsed and ignored them, and the result was "RIG DONE" with nothing moving. Both roles and wave rock are now
  rejected loudly on the fast path, in the window (gate + warning boxes with the workaround: mark the spinning
  source bone as Wheel, or disable the fast path) and in the script (hard error, like the existing Oar guard).
- **Typing a different source path into the Workshop resets the probe.** Only the Browse button cleared the
  part list — typing or pasting another file's path kept the previous probe's rows, checked node indices,
  preview and output path live, so Split would carve the *new* file by the *old* file's node indices and write
  over the old file's `_split.glb`. Any source change (however entered) now clears the probe state and asks
  for a fresh Probe; the output path re-derives from the new file.
- **The Workshop's distance merge recognizes diagonal dashes.** The direction gate — which stops two parallel
  dashed lines from fusing into one part through a near crossing — judged "elongated" by the axis-aligned
  bounding box, so a dash at 45° read as a blob (two equal extents), skipped the gate, and parallel diagonal
  rigging lines merged into one part. Elongation is now measured in the island's own frame (principal-component
  aspect, rotation-invariant); axis-aligned models behave exactly as before. Locked by a regression test that
  is the original gate fixture rotated 45°.
- **Browse's "Fix 100× oversize" auto-guess now actually reaches the bake.** The guess is a Lab-owned field,
  so on an already-saved entry the Factory's ownership rebase silently reverted it right before baking — the
  status line promised "carried by the next Bake" while the bake ran with the old value (a 100×-giant or
  floating result on metre-scale rigged GLBs). The guess now stays armed through the rebase until a Bake or
  "Save settings" persists it, after which the Animation Lab's checkbox owns the field again; picking another
  entry or using Make static disarms it.
- **The Factory's "Save settings" can no longer write dead baked-asset GUIDs.** The ownership rebase carried the
  *form's* copies of the skeleton/atlas/clip GUIDs into the save — but a Lab rebake of the same entry regenerates
  those GUIDs without refreshing an open Factory form, so a later "Save settings" wrote the old, dead ones next to
  the live role clips (unresolved-GUID warnings; the unit stopped injecting until the next bake). Baked GUIDs now
  always come from the registry's saved copy, and the button runs the save path that restores the full GUID family
  (which also brings the richer status line: what applies on load vs what still needs a Bake, and a note when the
  Model file differs from the last bake).
- **Cutout transparency survives on every atlas path, not just one.** The fix that kept alpha-mask foliage
  intact (transparent texels preserved, DXT5 chosen at compression) had landed only on the static
  multi-material branch — the animated multi-material path and the shared single-material path still forced
  every texel opaque, flattening cutout cards into solid triangles. All four paths now run the same detection
  (>1% transparent samples = intentional alpha); fully opaque sources bake byte-identical to before.
- **A failed albedo extraction now fails the bake instead of shipping a flat-grey model as a success.** On the
  animated path, the stale-extraction cleanup deletes every extracted albedo before re-running glbconv; when
  glbconv then failed (missing dotnet, a broken GLB), the bake logged one warning and carried on to a grey
  atlas, a green "Baked" toast, and an updated registry. The extraction failure is now a bake failure with the
  cause and the fix in the error text. (The static path already failed properly.)
- **"Reuse extracted files" now actually protects a single-material model's hand-edited albedo.** The
  protection (and the freshness test) hinged on the extraction's MTL file — which glbconv writes only for
  multi-material sources. A 1-material GLB therefore read as permanently stale: every bake deleted
  `<name>_albedo.png` (hand-edits included, the exact loss the checkbox prevents) and re-ran the extraction.
  Freshness is now judged by the extraction stamp, which both shapes get, and the checkbox protects whichever
  extraction shape exists. Keeping an extraction that no longer matches the source warns instead of staying
  silent.
- **A failed re-bake restore no longer destroys its own backup.** The rollback path deletes the current outputs
  before copying the backup back; if that copy then failed (a file locked by antivirus or an indexer), the cleanup
  still wiped the backup directory — old assets gone, partial new assets gone, backup gone, git the only recovery.
  The backup is now discarded only after a **successful** restore; on a failed one it is kept and the error names
  its path with copy-back instructions.

## 0.5.5 — 2026-09-04

- **Rowing — a galley oar bank, animated from merged meshes.** A new **Oar** role (hotkey `O`) in the Vehicle Lab.
  A galley's oars usually arrive as a few merged meshes — poles in one, blades in another (often split front/back) —
  each holding *every* oar across *both* banks. Mark those meshes Oar and, uniquely among the roles, one marked mesh
  becomes **many** bones: the rig recovers each individual oar (projecting the geometry onto the plane perpendicular
  to the common pole direction, where the oars separate cleanly — naive distance clustering fails because the poles
  converge at the oarlocks and fan to the blades), gives each a bone at its oarlock, skins it rigid, and bakes a
  unison stroke into `Spin` — a fore-aft **Sweep** about the oarlock plus a phase-locked **Dip** (blades drop on the
  aft drive, lift on the recovery), a seamless loop. The new **"Oars — a galley rowing"** section exposes **Sweep**,
  **Dip**, and **Stroke frames**, tuned against the preview loop. Adds one bone per oar (~60 on a full galley), well
  within the skeleton budget. The oars row whenever the movement clip plays. Validated headless on a 64-oar galley.
  Recovery tolerances and bank centre are derived from the marked geometry, so uniformly scaled or translated source
  models behave the same. When wave, wheel, and rowing periods differ, each motion is fitted to a whole number of
  cycles over the shared `Spin` range instead of freezing at its last key. Older recipes migrate to the rowing defaults;
  source-skeleton fast-path generation now blocks Oar roles with instructions to probe the mesh parts instead.
  Marked Oar meshes are excluded from BOTH double-siding paths, the global checkbox included — galley blades ship
  as authored front/back sheet pairs, and doubling them z-shimmers four near-coincident layers.
- **Fix inside-out faces — a targeted winding repair, not a blunt recalc.** A source whose side planking ships
  wrong-way-out (the Khalandion) reads see-through from outside while showing the far wall's interior. The new
  Vehicle Lab checkbox reverses the islands that provably face the hull's interior (judged against an axis through
  the hull *belly* — a bbox centre gets dragged to mast height and mis-judges the deck); everything the test cannot
  call decisively keeps the artist's winding, as do marked **Sail** and **Oar** meshes (a whole-model `Shift+N`
  recalc, and then a sheet-detection heuristic, were both tried and rejected — each flipped or missed authored
  surfaces). No extra triangles; weights and UVs untouched. Verified with backface-culled renders: deck solid from
  above, hull solid from both beams.
- **Sail — marked canvas, double-sided, switched on/off via its own `Furl` clip.** A new **Sail** role
  (dropdown; the `S` hotkey marks Structure): explicit marking replaces sail auto-detection outright. All sail parts weld to one `Sail` bone and are
  **always exported double-sided** (canvas reads from both tacks, artist winding untouched). The hide is its own
  generated **`Furl` clip** whose frame 1 **flips the canvas 180° below the keel** — rotation-only, the same
  Deploy-proven stance mechanism the trails use — used as a **stance, never played**. The clip format carries no
  visibility or alpha, so out-of-sight is the only disappear it can express; the clean on/off comes from never
  playing the move. (Four designs were rejected in the field: the hide keyed inside Spin's frame 0 twitched at
  every loop restart; a 12-frame visible descent read as the sail sinking through the deck; a 1-frame drop showed
  travel when the transition was played; and a translation-based stance fought the converter's rest-fold and
  location-strip and shipped misplaced in both clips.) Assign: Idle/reference = `Spin[0..0]` (defines the rest —
  never put `Furl` in the reference field, or the bind adopts the struck pose), Idle stance (override) =
  `Furl[1..1]`, Movement = `Spin`, **After-move / Pre-move empty** — the state change swaps the pose in one tick.
  Keep bone translations can stay OFF: the strike is pure rotation.
- **Blade roll — square feathered blades to the water.** Some sources model the oar blades *feathered* (flat face
  parallel to the stroke), so they knife through the water edge-on instead of scooping. The Oars section's new
  **Blade roll (deg)** spins each recovered oar about its own long axis in the rest geometry — the cylindrical
  pole shows no change, only the blade face turns, with no seam at the blade root. 0 (the default) leaves the
  source untouched; the Khalandion wants 90. Measured: the blade sheet normal turns by exactly the dialed angle.

- **Rigging — selective source-side decimation for rope geometry.** A new **Rigging** role: dense line/rope
  meshes are often a model's single biggest vertex sink while being barely visible at game distance (the
  Khalandion's ropes alone: 65k verts). Mark them Rigging and the new **Rigging reduce (%)** dial
  collapse-decimates exactly those parts at Generate, at the source — the previews, the clustering, the winding
  fix and the bake all see the slim mesh, and the Factory's global *Reduce to ~tris* budget stops being spent on
  invisible ropes. Per-part before/after vert counts are printed so an over-aggressive dial is loud, not silent.
- **Structure — a second reduction tier with its own dial.** Small-but-dense detail geometry (railings, a carved
  bow figure — another 65k verts each on the Khalandion) is more visible than rigging, so it takes its own,
  usually gentler **Structure reduce (%)**. Same dissolve+collapse treatment at Generate; both tiers print
  original → dissolved → final against the dial's target.
- **Body reduce (%)** completes the tiers — parts explicitly marked Body, default 0 (untouched: the hull is the
  model's face, and the Factory's global *Reduce to ~tris* is usually the smarter place to slim it).
- **Oar reduce (%) / Sail reduce (%)** extend the tiers to the animated roles (a galley's merged oar meshes are
  dense; sail canvas ships twice, once per side). Both run in the same pre-armature pass, so the per-oar
  clustering, bones and weights land on the slim mesh — verified identical cluster recovery at 0% and 50%.
  Defaults 0. Note the dials are floors: the dissolve pass can overshoot on flat canvas (a 30% sail dial cut 66%
  on the Khalandion), so any non-zero sail value already cuts hard.
- **Save can no longer silently eat a tuned recipe.** A reopened Vehicle Lab window starts with default knobs
  while still naming the saved recipe — one reflex Save then replaced a tuned rowing stroke with defaults.
  Overwriting a recipe this window has not read since it was opened now asks first, and every overwrite leaves
  a `<name>.json.bak~` backup beside the recipe (the `~` keeps Unity from importing it) — a confirmed mistake
  is one rename from recovered.
- **The Factory reports every baked mesh's quad count against the engine's draw ceiling** — Humankind renders a
  unit mesh as at most 255 sub-particles × 64 primitives = **16,320 quads**, and the overrun is silent in-game:
  the mesh stores fully, the last-baked parts (masts, rigging, sails) simply never draw (how the Great Galley
  shipped mastless through five bakes). After each Bake the console prints per-mesh `N quads — fits (M to spare)`
  or a warning naming the excess, and an over-ceiling bake also raises a dialog. Dial *Reduce to ~tris* against
  this line — no game launch needed to know.
- **The part filter + marking list is a foldable "Parts" section** — collapse it once the roles are decided to
  reach the preview and tuning sections without scrolling past 280px of rows; the header keeps the part count
  and how many are still undecided. (Folding it also parks the keyboard review loop.)
- **All five reduce dials + the two facing checkboxes now live in their own "Vertices control" section** —
  everything that reshapes exported geometry at Generate in one foldout, out of Spin where it had no business.
  The collapsed header summarizes the active facing fixes and each marked tier's dial.
- **Flag is now the OPPOSITE of sails** (2026-09-05): banners fly AT ANCHOR and are struck below the keel while
  the ship moves — one Flag bone held flipped through the whole Spin clip; the idle stance shows them at rest.
  Rudder split into its OWN parts file to keep the always-visible treatment (a rudder must never vanish).
- **Flag — double-sided like a sail, but never hidden.** Banners and pennants must read from both sides, yet a
  flag keeps flying at anchor — so the new role gets the sail's doubling and winding protection without the Furl
  strike: no bone, no clip, welded to the body. **Rudder** shares the exact treatment under its own name — a
  closed slab with one face-side wound inward scores ~0 in the inside-out flip (the halves cancel), so no
  whole-island flip can repair it; doubling can.
- **Positive Sweep rows forward** (2026-09-05 sign flip): the field-verified forward stroke needed a negative
  dial while positive Rake already shifted toward the bow — the sweep sense flipped so both dials agree that
  positive points at the bow. Recipes saved before the flip negate their Sweep once.
- **Sweep accepts negative** — if the galley rows backwards, flip the sign (the wheels' Spin-degrees convention);
  the dip phase stays put, so blades still bury on the reversed drive. **Sweep is the TOTAL arc**, split evenly
  about the rest rake: 24 = 12° forward + 12° back (it was a half-amplitude before — a 24 dial swung 48°, all of
  it reading as "backward" against the Khalandion's already-aft modelled rake). Slider range widened to ±90.
  **Dip accepts negative too** — it flips which half of the stroke is submerged, the second independent way to
  reverse the rowing direction (flip either Sweep or Dip, not both: both flips cancel). Keep |Sweep| above ~2× the
  dip, or the dip's fore-aft component on a raked oar drowns the sweep and the stroke churns instead of pulling.
- **Lift (deg)** re-centres the stroke height — a constant tilt about the dip axis with the dip oscillating
  around it. The knob the dip sign cannot be (±dip is the same oscillation, phase-flipped): a source whose oars
  are modelled raked steeply into the water rides too deep at any dip; positive lift brings the bank toward
  horizontal. Measured: lift 30 raises the Khalandion blade path ~0.5 units, deepest point 0.45 shallower.
- **Rake (deg)** — the horizontal twin: a constant fore/aft rotation about the oarlock re-centring the sweep
  ARC, with the sweep oscillating around it. A source that models the oars raked far aft (the Khalandion: ~50°)
  swings "all backward, nothing forward" at any sweep, because the arc is symmetric about that modelled rake;
  rake it toward the bow until the stroke straddles the perpendicular. Same sign convention as Sweep.
- **Pivot (%)** — where the oarlock, the fulcrum every stroke rotation happens about, sits along each oar
  (percentage of its inboard→outboard extent). 0 = at the handle, the whole oar swings; higher = further out,
  less oar moves and the handle counter-swings more. 30 was the hardcoded value through the whole build.
- **Length (%)** — stretches each oar along its own axis ABOUT the oarlock: the pivot stays planted at the
  hull, the blade reaches further out and down, the handle further in. Pure axial scale — blade width and pole
  thickness untouched. 100 = the modelled length.
- **The Animation Lab preview floats boats at the calibrated water level.** It drew its hex at ground height
  (-0.02) even for boat pawns — water-blue in colour, ground in height — so the Factory showed oar blades in the
  water while the Animation Lab showed them dry. Both panes now share the pack's one-source-of-truth
  `ModelRegistry.WaterLevel`; the forward arrow and reference man ride the same plane.
- **Split disconnected GLB parts** — a new `Tools ▸ HAF ▸ Model Tools ▸ Split disconnected GLB parts…` command
  turns every disconnected geometry island inside a GLB mesh node into its own selectable child part. The source
  is never overwritten. Materials, transforms, skins, animation targets, textures and original vertex data are
  preserved; the tool appends only filtered index accessors and child nodes, verifies the triangle total, and writes
  a new `<name>_split_parts.glb`. Duplicate vertices at UV/normal seams are welded with a tight scale-relative
  tolerance, so a visually continuous surface is not split merely because its shading data has a seam.
- **Rudder reduce (%) and Wheel reduce (%)** complete the source-tier set at seven. Rudders ship double-sided
  (every kept vertex counts twice), and CAD-style rims carry far more vertices than a spinning disc shows;
  both cuts run before doubling/clustering. Each reduced part keeps at least 8 verts, so triangle-soup wheels
  cut less than the dial asks — the Generate log prints the real totals.
- **Rolling-contact wheel speeds apply only to wheels that reach the ground.** Four propellers marked Wheel on
  a biplane: one spun at 540° because the 1/diameter ground-rolling scaling hit something hanging at wing
  height. A wheel cluster now scales only when its bottom lies within 15% of model height above the global
  minimum; airborne wheels (props, fans) keep the dialed degrees uniformly, and the log names the exemptions.
- **Preserve — a role that keeps a part exactly as authored.** No reduction, no inside-out flip, no doubling
  (the global Double-sided switch included); welds to the hull like Body but ships as its own untouched mesh.
  The escape hatch for parts every automatic pass keeps getting wrong.
- **Model Workshop — the aimed version of the splitter.** `Tools ▸ HAF ▸ Model Workshop`: Probe lists every
  mesh-carrying node with its triangle count and disconnected-island count; check exactly the parts hiding
  floating junk and Split writes a GLB where ONLY those become `_Part_NNN` children (same lossless method).
  Born from the galley: junk islands welded into hull-shared parts could not be marked Ignore in the Vehicle
  Lab, and the split-everything command exploded the rigging into ~1,500 parts. Feed the output to the Vehicle
  Lab, mark the junk Ignore, rig as usual.

## 0.5.4 — 2026-09-03

- **Double-sided for animated vehicles — now a Vehicle Lab option, applied at the source.** The engine culls
  backfaces, so a single-sided / CAD-style source (thin spokes, flat plates) renders see-through from the wrong
  angle. The Vehicle Lab gained a **"Double-sided (fix see-through parts)"** checkbox: when set, the rig export
  appends a reversed copy of every face to the Spin GLB itself, nudged slightly inward so front and back aren't
  coincident (coincident faces read ~50% transparent under the game's alpha-to-coverage shader). Bone weights are
  carried on the duplicated vertices, so the skeleton bake still validates. Because the fix lives in the source
  geometry, the rig, the preview meshes and the baked model are all the same vertex count — so it just works in
  the Vehicle Lab turntable, the Model Factory preview, the Animation Lab and in-game, with no runtime doubling
  and no preview special-casing. Doubles the triangle count; the Factory's **Reduce to ~tris** still caps the
  shipped mesh. (This replaced an earlier runtime-doubling attempt whose rig-vs-baked vertex-count mismatch caused
  a long string of preview glitches — half-rendered, grey, and partially-transparent models.)
- **The Model Factory's Double-sided checkbox is removed.** Double-siding for rigged models is a source-geometry
  concern owned by the Vehicle Lab; the Factory had no runtime doubling, so a checkbox there only did nothing for
  animated models. One fewer knob. (The static-bake path's own doubling and the `doubleSided` field remain for
  backward compatibility — existing entries bake as saved.)
- **The Animation Lab's runtime Position offset now shows on a *playing* clip too** — it was only applied to the
  static rest pose, so an offset model looked mispositioned in the Lab versus the Factory; and the domain-reload
  restore resumes the clip.

## 0.5.3 — 2026-09-02

- **A failed re-bake can no longer ship a mismatched normal map.** The output whitelist behind the E5
  rollback, the cross-path sweep, and Remove was missing the static path's three surface atlases
  (`_NormalAtlas` / `_RoughAtlas` / `_NormalAtlasPrev`) — so a re-bake that failed after packing them
  restored the *old* colour atlas next to the *new* normal atlas (different packing rects, silently wrong
  shading), and removing a model orphaned all three in the shipped Resources folder. Found by a critical
  review, not an incident.
- **The suffix list now exists exactly once.** Both bake-test cleanups carried their own hand-copies of that
  whitelist; the Tier-2 copy had already drifted (state-driven `_ClipsMove`/`_ClipsAttack`… outputs were never
  deleted, stranding throwaway fixtures in shipped Resources). Both now reference the baker's own list —
  a fourth place to update no longer exists.
- **A district that layers on a model's outputs is no longer swept in silence.** A district bake builds on a
  same-named model's `_Atlas` and overwrites the atlas trio with processed versions — so a model re-bake or
  Remove could yank those out from under it with no word said. The Remove dialog now names the layered district
  *before* the decision, and every sweep logs which district needs a re-bake afterwards.
- The E5 rollback test's fixture gained a `Textures/` normal map, so the restore assertion now covers the
  surface atlases too — the exact whitelist entries this release added would otherwise have stayed untested.

## 0.5.2 — 2026-09-02

- **Flat-colour swatches now actually load — no more red parts.** glbconv writes each untextured material's
  colour as an 8×8 `.tga` swatch, but both bake paths loaded albedos with `Texture2D.LoadImage`, which decodes
  only PNG/JPG: on the animated path every swatch silently became Unity's 8×8 **red** placeholder (the all-red
  Bell H-13 — this bug predates 0.5.0 and was the true root of the whole flat-colour saga), and on the static
  path swatches were skipped entirely, landing flat materials on the grey tile. Both loaders now share one
  decoder that reads glbconv's TGAs directly, and any file that still can't be decoded — or an MTL entry whose
  albedo file is missing — logs a loud `[Factory]` warning naming the file instead of baking a placeholder.

## 0.5.1 — 2026-09-02

- **Changing a model's source file can no longer bake against the previous source's extraction.** glbconv writes
  an MTL only for multi-material sources, so re-pointing an entry at a different file could leave a *chimera*
  extraction folder (old MTL + swatches, new stamp + albedo) that the next bake silently consumed — the first
  0.5.0 re-bake of the Bell H-13 sampled a leftover 256×32 palette strip from the previous source and came out
  dark chaos. On a source change every derived extraction artifact is now removed before re-extracting, each
  with its `.meta`, so Unity's refresh has no orphans to complain about (*Reuse extracted files* still protects
  hand-edited textures by skipping the refresh entirely).
- **A multi-material source baked with Material mode Single now warns**, naming the material count and the fix —
  before this the log said nothing while every part sampled one atlas whole.

## 0.5.0 — 2026-09-02

- **Flat-colour (untextured) multi-material models bake correctly — no external atlasing step.** A SketchUp-style
  model whose materials are pure colours (`glass`, `paint`, `copper`… with no texture) already got an 8×8 solid
  swatch per material in the packed atlas, but its submeshes kept their source UVs — which such models fill with
  garbage (islands parked anywhere, even outside 0..1), so faces sampled neighbouring rects (wrong colours) and
  part edges bilinear-sampled the padding between rects (grey fringes). The bake now pins every vertex of a
  flat-swatch submesh to the **centre of its rect** — one interior sample point, immune to bad UVs, seam folds,
  padding bleed and mip averaging. Applied on both the animated and static multi-material paths; the per-submesh
  bake log says `(flat swatch — UVs pinned to rect centre)` when it fires. Hand-editing an extracted swatch into
  a larger real texture returns that part to normal UV mapping automatically. (Driven by the Bell H-13: rigged in
  the Vehicle Lab, 10 flat materials, previously only bakeable after an external "flat-colour atlas" rebuild of
  the GLB.)

## 0.4.13 — 2026-08-25

- **A menu click is answered with a dialog.** Every outcome of `Check for Updates…` now shows one — *up to
  date*, *update already in progress*, *could not reach the repository*, *check failed* — because the person who
  clicked is looking at the menu, not the console. The daily automatic check stays a single console line, as
  promised.

## 0.4.12 — 2026-08-25

- Version-only bump: the live fixture for 0.4.11's in-flight latch. From an 0.4.11 install: *Check for
  Updates…* → *Update now* → click the menu again **during the fetch** — it should answer *"update to 0.4.12 is
  in progress"* instead of re-offering the update.

## 0.4.11 — 2026-08-25

- **No more stale "update available" during an update.** Between *Update now* and Unity's reload, the old
  version's code keeps answering the menu — Package Manager already shows the new version while the check still
  reports the old one, and a re-click re-offered an update that was already applied. The check now latches while
  a fetch is in flight (*"update to X is in progress — Unity will reload when it's done"*), unlatches itself the
  moment the running version matches, and a failed fetch clears the latch so checks are never wedged off.

## 0.4.10 — 2026-08-25

- Version-only bump: the live fixture for 0.4.9's one-click update. From an 0.4.9 install,
  `Tools ▸ HAF ▸ Check for Updates…` should show the *Update now* dialog for this release — the first update
  ever applied without opening Package Manager.

## 0.4.9 — 2026-08-25

- **The update check now applies the update.** `Tools ▸ HAF ▸ Check for Updates…`, on finding a newer release,
  offers *Update now* — one click hands the fetch to Package Manager (`Client.Add` with this install's own URL,
  the same operation as its Update button). The daily check stays a console line on purpose: an unrequested
  dialog on editor start is exactly the surprise this package promises not to be.

## 0.4.8 — 2026-08-25

- Version-only bump so the update check shipped in 0.4.6 could be verified live: an install on 0.4.7 should
  report this release via `Tools ▸ HAF ▸ Check for Updates…` and, within a day, unprompted in the console.

## 0.4.7 — 2026-08-25

- **This changelog exists**, and Package Manager's *View changelog* button now opens it. It pointed at a page
  that was never created (found by a user pressing the button — the URL was written plausible-looking and
  unverified).

## 0.4.6 — 2026-08-25

- **Updates announce themselves.** A git-installed package gets no update indicator from Unity — verified live:
  a new release, an editor restart and a Package Manager refresh all showed the installed version as current.
  The tools now check for themselves: once a day (installed packages only) they read `editor/package.json` from
  the package's own repository — one anonymous, read-only fetch, nothing sent — and print one console line when
  a newer release exists. `Tools ▸ HAF ▸ Check for Updates…` asks on demand. Disable with EditorPrefs
  `HAF.UpdateCheck = false`.
- Releases are now **tagged** (`editor-vX.Y.Z`), so an install can be pinned:
  `…?path=/editor#editor-v0.4.6`.
- Docs: `Installation.md` gained *Updating the tools* — updates never touch your authored data.

## 0.4.5 — 2026-08-25

- Version-only bump used to verify the update mechanism live (no code change).

## 0.4.4 — 2026-08-24

- **Bake-test progress you can actually watch.** Two bars inside the Bake Tests window (run level + step level),
  live during the synchronous run; the modal bar carries both levels and elapsed time; a 250 ms heartbeat keeps
  everything ticking during minutes-long Blender steps; and the run position rides in the throwaway fixture
  names (`__smoketest__03of14_…`) so even Unity's own *Importing…* dialog — which nothing can draw into — shows
  where the run is.

## 0.4.0 – 0.4.3 — 2026-08-24

- **The Blender helpers and the glbconv GLB/glTF importer ship inside the package** (`Tools~`). Before this,
  any `.glb` import — and every Blender-dependent bake — failed in an installed package because the helper
  scripts lived outside it.
- **Home vs. installed is decided by how the package is installed** (git/registry = consumer install;
  `file:`/embedded = the developer's working copy), not by whether it is one.
- Bake-test documentation: the three verdicts (SKIPPED is a first-class answer), the fresh-install baseline,
  and the what-needs-Blender table (`Factory-Manual.md` §11).

## 0.3.0 – 0.3.1 — 2026-08-24

- **Your pack is your own.** The tools read and write `haf_packs/<YourProjectName>` — starting empty — instead
  of a hardcoded pack. Before this, on any machine with ENC installed as a player mod, the tools showed and
  tried to re-bake ENC's models inside other projects. An installed package can no longer see or touch another
  mod's pack.
- **A clean install cannot fail a bake test.** Missing prerequisites (no models yet, no Blender) report SKIP
  with a named reason in an installed package; they stay loud failures in the development checkout.
- No ENC-specific names on an installed package's screens; neutral preference keys (`HAF.BlenderPath`,
  `HAF.BepInExConfig`) with the historical keys still read as fallback.

## 0.2.0 — 2026-08-24

- The package moves into the HumankindAssetFramework repository (`?path=/editor`) and ships its own
  `Haf.Schema.dll` — installing from the old location cloned 65 MB of another mod's content and then failed to
  compile for want of that assembly.
- **An installed package changes nothing until asked**: automatic backups, the asset-delete guard and console
  filtering all default off outside the development checkout, and a first-run console line says so.

## 0.1.0 — 2026-08-24

- First installable package (`package.json` + asmdef): `Window ▸ Package Manager ▸ + ▸ Add package from git
  URL…`. The first real install immediately found the `.meta` files missing (Unity generates them silently
  under `Assets/`, and cannot in an immutable package folder) — fixed, and gated so the class is extinct.
