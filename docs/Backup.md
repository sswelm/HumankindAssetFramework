# Backup &amp; Restore

**Menu:** `Tools ▸ HAF ▸ Backup and Restore` (window title *Backup &amp; Restore*).

A safety net for the working set git does **not** cover. The ENCReload repo tracks its code — including
`Assets/Scripts/Editor` and `Tools/`, since 2026-07-03 — and `Assets/Databases`; but the heavyweight rest — the
licensed source models, the baked assets, and the **live BepInEx runtime config the plugin reads** — lives on
disk with no version control. The window layers four independent protections over it, from "I clicked the wrong
thing" up to "the machine burned down."

## The four layers

| Layer | Trigger | What it writes | Retention |
| --- | --- | --- | --- |
| **Manual version** | *Back up now* button | Timestamped folder of the toggled groups + manifest | Never auto-deleted |
| **Daily auto-version** | First editor load of a day (>24 h since last; toggle, default ON) | Full backup of ALL groups — assets *and* configuration — on a background thread, same core as the button | Newest **3** `_auto_` versions kept; rotation logged |
| **Delete guard** | Any asset deletion under a protected root (toggle, default ON) | `_deleted_<timestamp>_<name>/` copy of the asset (+ `.meta`) *before* the delete proceeds | Never auto-deleted (prune by hand via *Delete*) |
| **Offsite zip** | Rides along with any manual/auto backup (optional: set the *Offsite folder*) | ONE `HAF_<timestamp>.zip` per backup, in a second — ideally cloud-synced — folder. **Skipped entirely when nothing changed** since the last upload (see *Space*) | Never overwritten |
| **Factory remove-undo** | The Model Factory's **Remove** (always) | `_removed_<timestamp>_<name>/` with the entry's JSON + the exact baked-output whitelist, taken BEFORE anything is deleted; restore via the **Undo remove** button next to Remove OR the window's *Factory remove snapshots* section — both do the full restore (baked assets + registry entry) through one shared implementation | Never auto-deleted |

Every layer feeds the **same restorable list**: each entry, whether manual, auto, or delete-guard, has a
**Restore** button and the same safety guards.

## Space — why a daily 1.4 GB backup costs ~65 MB

Two snapshots taken 3.5 hours apart were compared file by file: **18 of 4,076 files differed.** Every backup was
re-copying ~1.4 GB of which ~99.6% was byte-identical to the one before, and `source` alone — the licensed models,
which change rarely — is 971 MB of that. Two independent savings (2026-08-23), kept separate because they fail
differently:

**Locally, unchanged files are hard-linked** to the newest existing snapshot. A hard link is a second *name* for the
same bytes on the same volume, so an unchanged file costs **zero** additional space while each snapshot stays a
complete, independently browsable, independently restorable folder. Same idea as Time Machine or `rsync --link-dest`.
The first real run: 4,077 files, **3,966 hard-linked (1.2 GB saved), 111 copied (65.4 MB)**.

> **Two consequences worth knowing.** Explorer reports each snapshot at its full apparent size — it counts shared
> bytes once per name, so the folder still *looks* like 1.4 GB. And deleting an old snapshot frees only the blocks
> unique to it; the bytes survive while any other snapshot still names them. Both are normal, and both mean keeping
> old snapshots is now nearly free.
>
> **The rule that makes it safe: never write INTO a snapshot folder.** Editing a hard-linked file edits every
> snapshot sharing it. Nothing in HAF does — restore copies *out* of a snapshot into the live tree, and the
> delete-guard copies *in* from the live tree to a fresh folder — so neither writes through a link. Deleting is
> always safe. If the backup root ever moves to a different volume, linking silently degrades to full copies: costs
> space, never correctness.

**Offsite, an unchanged snapshot is not uploaded at all.** Each zip is a full ~1 GB, and daily uploads of the same
models fill a 15 GB cloud quota in about a week. A SHA-1 over every file's relative path, size and mtime is written
into each snapshot (`haf_signature.txt`) and beside each uploaded zip (`.sig`); a matching signature means the
existing zip already *is* this backup, so the zip is skipped. Deliberately **not** a hash of the file bytes — reading
1.4 GB to decide whether to upload 1 GB is a poor trade, and path+size+mtime is the same evidence the copy step
already trusts. An absent or unreadable signature always proceeds: *"I don't know"* must never be read as
*"unchanged"*. The sidecar is written only after the zip is verified and moved into place, so a crash mid-zip cannot
leave a signature claiming an upload that never landed.

## A missing destination is a failure, not a thing to create

Both destinations used to be created on demand. Found live on 2026-08-23: the offsite folder was renamed and
**nothing anywhere said a word** — the next backup would have silently recreated it and reported
*"Offsite: zipped N files → …"*, a completely successful-looking line about a folder nothing syncs.

- **Offsite folder missing** → the zip is **skipped** with a message naming the path and the likely cause (renamed,
  moved, drive/share unavailable). The local backup is complete and untouched.
- **Backup root missing** → the backup is **aborted** before anything is written. Creating the new *timestamped*
  folder is correct; creating its *parent* is not — that silently rebuilds an empty history while every real snapshot
  sits under the old path, with the *Existing backups* list simply going empty.

Both also warn **in the window** the moment the path is missing, not only in the status line after a run — because the
daily auto-version runs unattended and nobody would ever read it.

## What it captures

Each group is an independent toggle with a live size readout (the daily auto-version always takes ALL groups):

| Group | Source |
| --- | --- |
| Editor scripts | `Assets/Scripts/Editor` |
| Source models | `Assets/FactorySource` (the bake *inputs* — licensed, irreplaceable, never shipped in the mod) |
| Baked assets | `Assets/Resources` (skeletons, atlases, clip collections, PNGs) |
| ENC Databases | `Assets/Databases` |
| Tools | `Tools/` (Blender rig/convert scripts, `glbconv`) |
| Runtime config | `BepInEx/config/haf_*.json` + **`haf_packs/` (the model registry — MISSING until 2026-08-17, found mid recovery-drill)** + `haf_skins/` + `haf_sounds/` (the regenerable `haf_atlas_dump/` is skipped) |

## The delete guard

Before *anything* under `FactorySource` / `Databases` / `Scripts/Editor` is deleted — the Factory's **Remove**
flow, a Project-window delete, a script — it is first copied to a `_deleted_<timestamp>_<name>` folder with a
real manifest, so the **Restore** button puts it back in one click, **including the `.meta`** (the asset keeps
its GUID, so references to it survive the round trip). The delete then proceeds normally; the guard never blocks
anything — it only makes every deletion undoable. Same-second deletions of same-named assets get uniquified
folders (no silent merge).

`Assets/Resources` is deliberately **not** guarded: the bake pipeline delete-firsts baked assets on every
re-bake (~30 delete sites), so guarding them would flood the backup root with churn within days — and bakes are
regenerable from FactorySource + config anyway. The daily auto-version still snapshots Resources for
go-back-a-version. **Preview-scratch files are excluded too** (`*_PropFit.prefab`, `*_Preview.prefab`,
`*_PreviewMesh/_PreviewMat`): the Lab's fit preview delete-first *rebuilds* them constantly ("NOT shipped;
preview-only"), and logging that churn as deletions alarmed exactly the person the guard should reassure — the
guard protects what can't be rebuilt, not what rebuilds itself.

## How a backup is stored

A backup is a new folder `<backup root>\<yyyy-MM-dd_HHmmss>\` (root is configurable and remembered; autos are
prefixed `_auto_`, guard snapshots `_deleted_`, pre-restore safety snapshots `_prerestore_`). Inside, each source
is copied under `<group>/<name>`, alongside a **`manifest.txt`** recording every source's *original absolute
path*, file count, and byte size. Backups are **never overwritten** (each is a fresh timestamp). After copying,
the file count is re-verified against the manifest — a mismatch is flagged loudly, and a mismatched backup is
never used as a restore's safety snapshot nor zipped offsite. **Critical-content verify**: a snapshot that took
the config group must actually CONTAIN every live pack registry (`haf_packs/*/pack.json`) or the whole backup is
marked not-ok with a loud message — a green backup literally says *"registry verified in snapshot"* (the recovery
drill found the registry silently absent from every backup for weeks; that failure mode is now structurally
impossible).

**The list** is grouped, every row starting with its date-time, newest first: *Full backups* (manual + daily
auto), then *Pre-restore*, *Delete-guard* (open by default — the section you check after an "oops"), and
*Factory remove* sections as counted foldouts. Restoring anything **auto-refreshes every open Model Factory
window** — no manual Refresh after a restore.

## Restore — selective, and guarded three ways

**The group checkboxes scope Restore exactly as they scope Backup** (2026-08-17, closing the all-or-nothing gap:
recovering one thing from an older snapshot used to roll every other group back to snapshot time). Tick only
"Baked assets" + "Runtime config" and a restore touches nothing else — the confirm dialog states the scope.
`_deleted` and `_prerestore` snapshots restore whole (they are single-purpose by nature). Restore is also
**smart**: only files that are *missing* or *actually different* (byte-compared) are written — identical files
are left untouched (so Unity doesn't re-import hundreds of unchanged assets) — and the status reports all three
counts ("Restored X missing + Y changed; Z identical untouched"). Restore reads the backup's manifest and copies
each selected source back to its original path, never at the cost of current work:

1. **Auto pre-restore snapshot.** Before touching anything, the *current* state of exactly the paths about to be
   overwritten is saved to a `_prerestore_<timestamp>` backup. A wrong restore is always undoable — just restore
   that snapshot.
2. **Additive only.** Files present in the backup overwrite their current versions, but any file you have **added
   since** (not in the backup) is left untouched. New work can't vanish. There is deliberately no destructive
   "mirror/clean" mode.
3. **Explicit confirmation.** A dialog lists exactly which original paths will be written before anything happens,
   and the restored file count is reported afterward. `AssetDatabase.Refresh()` reimports the restored assets.

`Delete` removes a backup folder only (live files untouched), after a confirm.

## The offsite zip

`D:` is a second disk in the **same machine** — a machine-level event (theft, fire, surge) takes the backups with
the originals. The code is safe on GitHub; the source models and bakes are not. Set the **Offsite folder**
(ideally cloud-synced: OneDrive/Drive/NAS) and each backup is also written there as ONE `HAF_<timestamp>.zip`:

- **Optional** — blank folder = off — and **silent**: zipped on a background thread, so a multi-GB snapshot never
  freezes the editor; the result lands in the status line (or the Console if the window was closed meanwhile).
- **Atomic** (`.partial` then rename) and **never overwritten** — and **self-recovering** (2026-08-19, found
  by the first end-to-end offsite verify): a domain reload (any recompile) kills the background zip thread
  mid-write, which used to leave a stale `.partial` and a backup silently missing its offsite copy. On every
  editor load, stale partials are cleaned up and any backup folder without its final zip is re-zipped
  automatically (logged in the Console).
- **Verified**: the finished zip is re-opened and its file count compared to the snapshot's — a mismatch deletes
  the partial and says so loudly rather than leaving a corrupt archive to be discovered the day it's needed.
- *Zip latest backup → offsite now* covers pre-existing snapshots; the daily auto-version zips itself when the
  folder is set.

## Known limits (accepted, on the record)

- The delete guard hooks Unity's asset pipeline (`AssetModificationProcessor`) — a raw `File.Delete` from a
  script or a deletion in Windows Explorer bypasses it. Nothing an editor script can catch; the daily
  auto-version is the net under that net.
- Deleting a very large *folder* copies it synchronously before the delete proceeds (a pause proportional to its
  size — arguably the pause you want before deleting 900 MB of source models).
- The background auto-version could copy a file mid-write if a bake runs at the exact moment of editor load — a
  torn copy of one file in one auto-version, healed by the next day's version.
- `_deleted_` snapshots accumulate until pruned by hand (that's the never-lose rule; the *Delete* button is the
  valve).

## Notes

- The window itself lives in `Assets/Scripts/Editor` (git-tracked in ENCReload since 2026-07-03), so its own
  **Editor scripts** group is belt-and-braces — it still captures uncommitted editor-tool edits.
- Unity `.meta` files are copied with their assets, so restored assets keep their GUIDs/import settings.
- Directory sizes are cached; hit **↻ sizes** to recompute after big changes.
- The feature was critically reviewed on ship day (2026-08-17): four real defects found and fixed pre-ship — the
  Resources-churn flood, a same-second manifest collision, a synchronous 1+ GB auto-copy freezing editor load,
  and dead Restore buttons on guard snapshots. See the CHANGELOG entry for the full story.
