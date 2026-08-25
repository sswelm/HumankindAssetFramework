# Changelog — HAF Authoring Tools

The **package** changelog: what changed for someone who installs the tools. (The project-wide engineering log
lives in the repository's root `CHANGELOG.md`.) Versions are also git tags: `editor-vX.Y.Z`.

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
