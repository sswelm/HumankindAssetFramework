# Backup &amp; Restore

**Menu:** `Tools ▸ HAF ▸ Backup and Restore` (window title *Backup &amp; Restore*).

A safety net for the working set git does **not** cover. The ENCReload repo tracks its code — including
`Assets/Scripts/Editor` and `Tools/`, since 2026-07-03 — and `Assets/Databases`; but the heavyweight rest — the
licensed source models, the baked assets, and the **live BepInEx runtime config the plugin reads** — lives on
disk with no version control. This window snapshots all of it (tracked code included, so one folder restores
everything together) to a timestamped folder on `D:`.

## What it captures

Each group is an independent toggle with a live size readout:

| Group | Source |
| --- | --- |
| Editor scripts | `Assets/Scripts/Editor` |
| Source models | `Assets/FactorySource` (the bake *inputs* — never shipped in the mod) |
| Baked assets | `Assets/Resources` (skeletons, atlases, clip collections, PNGs) |
| ENC Databases | `Assets/Databases` |
| Tools | `Tools/` (Blender rig/convert scripts, `glbconv`) |
| Runtime config | `BepInEx/config/haf_*.json` + `haf_skins/` + `haf_sounds/` (the regenerable `haf_atlas_dump/` is skipped) |

## Automatic (2026-08-17) — both silent, both optional, both feeding the same restorable list

- **Delete guard** (default ON): before *anything* under `FactorySource` / `Databases` / `Scripts/Editor` is
  deleted — the Factory's Remove flow, a Project-window delete, a script — it is first copied to a
  `_deleted_<timestamp>` folder in the backup root with a REAL manifest, so the window's **Restore** button puts
  it back in one click (incl. the `.meta`, keeping the GUID). The delete then proceeds normally; the guard never
  blocks anything. `Assets/Resources` is deliberately **not** guarded: the bake pipeline delete-firsts baked
  assets on every re-bake (~30 delete sites) — guarding them would flood the backup root with churn within days,
  and bakes are regenerable anyway (the daily auto-version still snapshots them).
- **Daily auto-version** (default ON): on the first editor load of a day, a full silent backup of ALL groups —
  assets *and* configuration — runs through the same core as the button, so it appears in the backups list with
  its own **Restore** button like any manual version ("go back versions"). The offsite zip rides along if
  configured. **Retention:** only the newest **3** `_auto_` versions are kept (rotation is logged); manual
  backups, `_prerestore` and `_deleted_` snapshots are never auto-deleted.

## How a backup is stored

**Back up now** writes a new folder `D:\HAF_Backups\<yyyy-MM-dd_HHmmss>\` (destination is configurable and
remembered). Inside, each source is copied under `<group>/<name>`, alongside a **`manifest.txt`** that records
every source's *original absolute path*, file count, and byte size. Backups are **never overwritten** (each is a
fresh timestamp) and **never auto-deleted**. After copying, the file count is re-verified against the manifest
and reported — a mismatch is flagged loudly.

## Restore — guarded three ways

Restore reads a backup's manifest and copies each source back to its original path, but never at the cost of
current work:

1. **Auto pre-restore snapshot.** Before touching anything, the *current* state of exactly the paths about to be
   overwritten is saved to a `_prerestore_<timestamp>` backup. A wrong restore is always undoable — just restore
   that snapshot.
2. **Additive only.** Files present in the backup overwrite their current versions, but any file you have **added
   since** (not in the backup) is left untouched. New work can't vanish. There is deliberately no destructive
   "mirror/clean" mode.
3. **Explicit confirmation.** A dialog lists exactly which original paths will be written before anything happens,
   and the restored file count is reported afterward. `AssetDatabase.Refresh()` reimports the restored assets.

`Delete` removes a backup folder only (live files untouched), after a confirm.

## Notes

- The window itself lives in `Assets/Scripts/Editor` (git-tracked in ENCReload since 2026-07-03), so its own
  **Editor scripts** group is belt-and-braces — it still captures uncommitted editor-tool edits.
- `D:` is a second disk in the **same machine** — a machine-level event (theft, fire, surge) takes the backups
  with the originals. The tracked code is safe on GitHub; the source models and bakes are **not**. That gap is
  closed by the **offsite copy** (2026-08-17): set the *Offsite folder* (ideally a cloud-synced folder like
  OneDrive/Drive) and each backup is also written there as ONE `HAF_<timestamp>.zip` — **optional** (blank = off)
  and **silent** (zipped on a background thread so a multi-GB snapshot never freezes the editor; the result lands
  in the status line). Zips are atomic (`.partial` then rename), never overwritten, and **verified** (the zip is
  re-opened and its file count compared to the snapshot's — a mismatch deletes the partial and says so loudly).
  A *Zip latest backup → offsite now* button covers pre-existing snapshots.
- Unity `.meta` files are copied with their assets, so restored assets keep their GUIDs/import settings.
- Directory sizes are cached; hit **↻ sizes** to recompute after big changes.
