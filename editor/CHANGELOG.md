# Changelog — HAF Authoring Tools

The **package** changelog: what changed for someone who installs the tools. (The project-wide engineering log
lives in the repository's root `CHANGELOG.md`.) Versions are also git tags: `editor-vX.Y.Z`.

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
