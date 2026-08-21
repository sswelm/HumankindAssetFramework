# HAF multi-mod — shipping your own pack

The runtime (this plugin) is a **Humankind Asset Framework** host: it loads not just ENC's registry but **any number of packs**,
so a modder augments their own units with a custom 3D model, texture, and sound **without touching ENC's files or code**. You
ship a config file plus your assets; the runtime discovers, merges, and reports.

This is the *loader* contract. For how to **bake** a model into the assets a pack references, see [`Factory-Manual.md`](Factory-Manual.md).

## What a pack is

A pack is one JSON file with the schema wrapper around the familiar `models` array:

```json
{
  "schemaVersion": 1,
  "modId": "yourmod",
  "dependsOn": [],
  "loadAfter": ["enc"],
  "overrides": [],
  "models": [ /* the same model entries the Factory writes — see Factory-Manual.md */ ]
}
```

A copy-ready starting point is [`haf-pack.example.json`](haf-pack.example.json).

| Key | Meaning |
|---|---|
| `schemaVersion` | The HAF schema version this file targets. Currently `1`. Evolves **additively** — new keys are added, old files keep loading. |
| `modId` | Your pack's unique id. Keep it stable; it's how you're named in the load report and how others depend on you. |
| `models` | Your model entries — identical to what the Factory bakes. Runtime-only entries (a retexture/tint/sound with no baked mesh) need no GUIDs. |
| `dependsOn` | modIds your pack **requires**. A missing dependency means your pack is **skipped** (loudly, in the log + report). Also orders you after them. |
| `loadAfter` | modIds your pack must load **after** (soft: an absent modId is ignored, your pack still loads). |
| `module` / `moduleGuid` | *(optional)* the Humankind runtime **module** your pack extends — packs load in the game's own mod order (see below). Defaults to your pack's **folder/file name** (== the module Name by convention), so you usually set nothing. Declare `module` (the module Name) only if your pack folder differs from your mod's name; `moduleGuid` is the stable key that survives a retitle. |
| `overrides` | explicit `{modId, pawnDescription}` replacements: your entry **replaces** that pack's entry on that pawn. Declared = consensual; without it, the clash is a conflict and the first-loaded entry wins. |

**Backward compatible:** a legacy bare `{ "models": [...] }` with no wrapper still loads — it just gets default metadata
(`modId` = the filename, `schemaVersion` = 0). A legacy `haf_models.json` base file, if one exists, still loads too;
ENC itself now ships as a normal pack at `haf_packs/ENCReload/pack.json` (`modId` `enc`, module `ENCReload`).

> **Naming — framework vs packs (deliberate):** everything FRAMEWORK-level is `haf_*` (`haf_packs/`,
> `haf_load_report.txt`); everything `haf_*` (`haf_models.json`, `haf_sounds/`, `haf_skins/`) belongs to **ENC the
> pack** — the reference pack, branded like any pack should be. Your pack's files carry *your* name (your folder,
> your modId); you never touch an `haf_*` path. The framework identity is fully neutral since 2026-07-19:
> `HumankindAssetFramework.dll`, GUID `community.humankind.haf`, menu root `Tools ▸ HAF`.

## Where it goes

- **ENC (the reference pack):** `BepInEx/config/haf_packs/ENCReload/pack.json` (`modId` `enc`) — a normal self-contained pack, ordered by its `ENCReload` Humankind module like any other. *(A legacy `haf_models.json` base file is still read if one exists.)*
- **Your pack, self-contained (recommended, since 2026-07-19):** one directory in `haf_packs/`:

  ```
  BepInEx/config/haf_packs/
    mymod/
      pack.json       ← your registry (default modId = the folder name)
      sounds/         ← your custom WAVs   (soundFile / soundStartFile / soundStopFile)
      skins/          ← your retexture PNGs (textureFile)
  ```

- **Or a flat file:** `haf_packs/mymod.json` — file-based assets then resolve from a sibling folder
  `haf_packs/mymod/` (`sounds/`, `skins/`), if present.

> **Assets:** baked mesh/skeleton/atlas resolve by Amplitude **GUID**, so they work from any mod's bundle the game loads —
> the runtime doesn't care which mod shipped them. *File-based* assets (custom WAVs, PNG skins) resolve **relative to the
> owning pack first** (`sounds/` / `skins/` in your pack folder), falling back to the legacy shared `haf_sounds/` and
> `haf_skins/` folders — so old packs keep working, and a new pack ships as one self-contained directory that never
> collides with another mod's filenames.

## How packs merge (resolution ENFORCED since 2026-07-19)

1. **Discovery** — a legacy `haf_models.json` base file (if any), then every `haf_packs/*.json` and `haf_packs/<mod>/pack.json`.
2. **Duplicate `modId`s** — the first file keeps the id; later same-id packs are **skipped** (log + report).
3. **`dependsOn` validation** — a pack whose dependency isn't loaded is **skipped** (iterated: skipping one pack can
   invalidate a pack that depended on it).
4. **Humankind load order** — packs are ordered to match **the game's own mod load order**. Each pack is matched to its
   Humankind runtime **module** (by `moduleGuid`, else `module`, else the pack's folder/file name == the module Name),
   and packs sort by that module's load-order index. A pack with **no matching module** — or when the game's module list
   can't be read — keeps alphabetical order after the matched packs. *HAF borrows Humankind's ordering instead of
   inventing one, so your mod manager decides who loads first.*
5. **`dependsOn` + `loadAfter`** — a **stable topological sort** layers your declared constraints *on top of* the HK
   order; a dependency **cycle** is broken loudly (its members fall back to the HK/file order).
6. **Merge** — all `models` are combined. A model's identity is its **`pawnDescription`** (the physical pawn slot — two skins
   can't ride one pawn).
7. **Declared overrides** — an entry whose pack declares `{modId, pawnDescription}` for the current owner **replaces**
   that entry (logged + reported as an override, not a conflict).
8. **Undeclared conflicts** — the **first-loaded pack wins** (first = earlier in Humankind's mod order), logged loud.
   *No implicit overrides* — declare it in `overrides` if the replacement is intentional.
   **`disabled: true`** on an entry is honoured on *every* path (since 2026-08-21): the entry never merges, so the
   original unit renders — and if it was a **declared override**, the prior owner keeps the pawn and the load report
   says so (`DISABLED: pawn=… 'b' is disabled; 'a' keeps the pawn`). That's the switch for testing an override
   against the original without editing two packs.
9. **The tuning tables** (`unitScales`, `eraGrid`, `formationThresholds` — see [Unit-Size.md](Unit-Size.md) and
   [Formations.md](Formations.md)) are read from **the same resolved, ordered pack list** as the models (since
   2026-08-21 — before that they were scraped from the raw file list, so a *skipped* pack still resized units and
   "later wins" meant alphabetical). Their cross-pack rules, each one **named** in the load report as a `TUNING:` line
   and a `[Resize] cross-pack:` warning:
   - `unitScales` — rules **multiply**, across packs too; when two packs' rules share a `match`, the report names both
     factors and the composed product (×0.6 × ×0.5 → ×0.3). Nothing is dropped; the point is that it's never silent.
   - `eraGrid` — each **row** (unit era) belongs to the **last** pack in mod order that authors it.
   - `formationThresholds` — the **whole table** belongs to the **last** pack in mod order that authors one.

**Not built (deliberately):** a `patches` concept — field-level modification of another pack's entry, as opposed to
`overrides`' whole-entry replacement. It would let compatibility packs tweak one knob without duplicating a full model
definition; queued until a real compatibility pack needs it, so its shape is driven by a real use case.

## The load report

Every load writes **`BepInEx/config/haf_load_report.txt`** — the first thing to check after adding your pack:

```
HAF load report  (regenerated every load)
packs=2  models=14  conflicts=0  overrides applied=0

[enc]      schemaVersion=1  models=13  file=pack.json
[yourmod]  schemaVersion=1  models=1   file=yourmod.json

RESOLUTION:
  HK module order: enc #1→ENCReload → yourmod #4→YourMod
```

The `RESOLUTION` line shows the resolved order and how each pack matched its Humankind module (`enc #1→ENCReload` = the
`enc` pack matched module `ENCReload` at load-order index 1). `yourmod (no matching module — alphabetical)` there would
mean HAF couldn't tie your pack to a game module — check that your pack folder matches your mod's name, or set `module`.

If your pack isn't listed, it wasn't discovered (wrong folder, a parse error — check the BepInEx log), **or it was
skipped by resolution** (duplicate `modId`, missing `dependsOn` — the `RESOLUTION` section says which and why). An
`OVERRIDES APPLIED` section lists declared replacements that took effect; a `CONFLICTS` section means two packs are
fighting over a pawn undeclared.
