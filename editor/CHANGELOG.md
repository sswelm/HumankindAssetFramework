# Changelog — HAF Authoring Tools

The **package** changelog: what changed for someone who installs the tools. (The project-wide engineering log
lives in the repository's root `CHANGELOG.md`.) Versions are also git tags: `editor-vX.Y.Z`.

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
- **Fix inside-out faces — a targeted winding repair, not a blunt recalc.** A source whose side planking ships
  wrong-way-out (the Khalandion) reads see-through from outside while showing the far wall's interior. The new
  Vehicle Lab checkbox reverses the islands that provably face the hull's interior (judged against an axis through
  the hull *belly* — a bbox centre gets dragged to mast height and mis-judges the deck); everything the test cannot
  call decisively keeps the artist's winding, as do marked **Sail** and **Oar** meshes (a whole-model `Shift+N`
  recalc, and then a sheet-detection heuristic, were both tried and rejected — each flipped or missed authored
  surfaces). No extra triangles; weights and UVs untouched. Verified with backface-culled renders: deck solid from
  above, hull solid from both beams.
- **Sail — marked canvas, double-sided, switched on/off via its own `Furl` clip.** A new **Sail** role (hotkey
  `S`): explicit marking replaces sail auto-detection outright. All sail parts weld to one `Sail` bone and are
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
- **Flag — double-sided like a sail, but never hidden.** Banners and pennants must read from both sides, yet a
  flag keeps flying at anchor — so the new role gets the sail's doubling and winding protection without the Furl
  strike: no bone, no clip, welded to the body. **Rudder** shares the exact treatment under its own name — a
  closed slab with one face-side wound inward scores ~0 in the inside-out flip (the halves cancel), so no
  whole-island flip can repair it; doubling can.
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
- **The Animation Lab preview floats boats at the calibrated water level.** It drew its hex at ground height
  (-0.02) even for boat pawns — water-blue in colour, ground in height — so the Factory showed oar blades in the
  water while the Animation Lab showed them dry. Both panes now share the pack's one-source-of-truth
  `ModelRegistry.WaterLevel`; the forward arrow and reference man ride the same plane.

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
