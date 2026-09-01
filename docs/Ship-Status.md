# Ship Status — which bakes has the game not seen yet?

This page covers the **Bake → Build** boundary. For every authoring boundary—including registry-only Save and live
dials—see [Authoring state and deployment](Authoring-State-and-Deployment.md).

**Tools ▸ HAF ▸ Ship Status** (in the ENCReload Unity project). Built 2026-08-18, the same evening its reason
for existing was caught live: a submarine re-baked at 19:34 against a mod build from 19:29 — five minutes of
staleness, and the game resolved a dead skeleton GUID (the boot pre-flight's warning:
*"authored GUID did not resolve to a Skeleton asset (was it baked and shipped?)"*).

## The trap it closes

Baked assets — the `_Skeleton` / `_Atlas` / `_ModelMesh` / `_Mat` / `_Model.prefab` (+ clip) outputs in
`Assets/Resources` — reach the game **only through a mod build**. Everything else updates instantly: the
registry, skins and sounds are read straight from `BepInEx/config` on every launch. That split is easy to
forget mid-flow: you re-bake a model, relaunch the game, and the game silently loads the *previous* build's
assets — or, when the re-bake minted new asset GUIDs, resolves nothing at all.

Nothing in the editor surfaced which bakes the game had not seen. Now two things do, driven by **one shared
core** (so they can never disagree), which reuses the baker's own output whitelist (so a future change to what
a bake writes is picked up automatically).

## Surface 1 — the inline notice in the Model Factory

The selected entry shows **"Baked, but NOT in the mod build yet"** whenever its newest baked output is newer
than the newest mod build. It is an info box, not a warning — this is the *normal* state right after every
bake — and it names the fix: run the mod build, then relaunch. It refreshes at the same trigger points as the
coherence banner (selection, reload, bake, Refresh), never per-frame.

## Surface 2 — the Ship Status window

One row per baked thing across **all three registries** — units (`pack.json`), districts
(`haf_districts.json`), props (`haf_props.json`) — plus hand-prop names referenced from unit entries. (The
first version knew only units and accused every district and prop of being an orphan; caught by the user's
first-run screenshot.) The header shows the newest build's time and folder. States, problems sorted first,
every row with a tooltip:

| State | Meaning | Fix |
|---|---|---|
| **BAKED, NOT BUILT** | outputs newer than the newest mod build — the game still loads the previous assets | run the mod build, relaunch |
| **BAKE MISSING** | the entry authors asset GUIDs but no outputs exist | re-bake the entry |
| **ORPHANED BAKE** | outputs no registry owns (renamed/removed entries leave these) — dead weight that still ships | tick + Delete selected |
| **TEST ARTIFACT** | `__convgate__*` ConversionGateTest scratch — also ships | tick + Delete selected |
| shipped | in the current build | — |
| no bake needed / no bake yet | retex/borrow entries; saved-but-unbaked district/prop recipes | — |

**When it does not know where the mods are.** Every verdict above depends on finding Humankind's Community
folder. That path used to be a hardcoded `const`, so off the one machine it named, the window reported
*"Last mod build: NONE FOUND"* — which reads as *"you have not built the mod"* when the truth was *"I do not
know where to look."* It is now resolved (`HafPaths`): a saved override, else `<Documents>/Humankind/Community`.
When it still cannot be found the window says so and offers **Locate…**, a folder picker whose choice is
remembered. Pick the folder your other Humankind mods are already in.

`Environment.GetFolderPath` rather than a literal `%USERPROFILE%\Documents\…` on purpose: Documents is
routinely OneDrive-redirected to another drive and **localized** (on the machine this was written it is
`D:\OneDrive\Documenten`, with `Humankind` inside it a junction to `C:\GameData`). A literal path is wrong three
ways there; the API performs the shell's own resolution and follows all three.

## Selecting and deleting

Every row that has baked output files is selectable, list-style: **plain click** selects that row alone,
**Ctrl-click** toggles it, **Shift-click** selects the range from the last clicked row; the checkbox and
**Tick all** drive the same state, and selected rows are tinted.

**Delete selected** removes the ticked rows' baked outputs after a confirm dialog that lists the names.
Safety properties:

- deletion runs the baker's own output whitelist — never a name wildcard (the lost-portrait lesson);
- the **delete-guard snapshots every file first** — everything is restorable from the Backup & Restore window;
- a registry-owned entry is only **un-baked**: the entry stays and shows BAKE MISSING until re-baked.
  Removing an entry itself remains the Factory's **Remove** (its own confirm + recycle-bin flow);
- after a delete the window re-scans and nudges every open Factory window immediately.

## Relationship to the other guards

- The **boot pre-flight** (`haf_load_report.txt`, `## Pre-flight`) is the end-user-side detector of the same
  trap — it warns when a shipped registry references GUIDs the game cannot resolve. Ship Status is the
  author-side view, *before* launching.
- The **pack validator** checks content (bones, files, pawns, ranges); Ship Status checks *freshness*.
  A pack can validate clean and still be stale.
- The **delete-guard** and **Backup & Restore** make Ship Status's delete a recycle-bin operation, not a rm.

## Limits (known, accepted)

- Staleness is judged by **file timestamps** (newest output vs newest file in the newest
  `Community/ENCReload.*` build). Copying files around with preserved timestamps can fool it.
- The build location is the game's `Community` folder — the same place the headless `BuildMod` deploys to.
- "no bake yet" district/prop recipes are quiet (severity 0): unlike units, their recipes routinely exist
  before their first bake.
