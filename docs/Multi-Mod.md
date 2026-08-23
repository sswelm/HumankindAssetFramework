# HAF multi-mod — shipping your own pack

The runtime (this plugin) is a **Humankind Asset Framework** host: it loads not just ENC's registry but **any number of packs**,
so a modder augments their own units with a custom 3D model, texture, and sound **without touching ENC's files or code**. You
ship a config file plus your assets; the runtime discovers, merges, and reports.

This is the *loader* contract. For how to **bake** a model into the assets a pack references, see [`Factory-Manual.md`](Factory-Manual.md).

> **One honest caveat, and it is on the *authoring* side only.** Everything on this page — discovery, resolution,
> merge, conflicts, declared overrides, the tuning tables — is fully multi-pack **today**, and every runtime-only entry
> (retexture, tint, sound, formation, unit size) needs no bake at all: hand-write the JSON below and you are done. The
> **authoring tools**, however, still write one hardcoded pack identity (`haf_packs/ENCReload`, `modId: "enc"`), because
> they live inside the ENCReload Unity project. That is deliberate until the tools are packaged — see
> [Decisions](Decisions.md) — so baking a *new model* into *your own* pack is the one step that waits on it.

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
| `schemaVersion` | The HAF schema version this file targets. Currently `1`. Evolves **additively** — new keys are added, old files keep loading. Checked at load against the version the installed HAF implements; see [The schema contract](#the-schema-contract). |
| `modId` | Your pack's unique id. Keep it stable; it's how you're named in the load report and how others depend on you. |
| `models` | Your model entries — identical to what the Factory bakes. Runtime-only entries (a retexture/tint/sound with no baked mesh) need no GUIDs. |
| `dependsOn` | modIds your pack **requires**. A missing dependency means your pack is **skipped** (loudly, in the log + report). Also orders you after them. |
| `loadAfter` | modIds your pack must load **after** (soft: an absent modId is ignored, your pack still loads). |
| `module` / `moduleGuid` | *(optional)* the Humankind runtime **module** your pack extends — packs load in the game's own mod order (see below). Defaults to your pack's **folder/file name** (== the module Name by convention), so you usually set nothing. Declare `module` (the module Name) only if your pack folder differs from your mod's name; `moduleGuid` is the stable key that survives a retitle. |
| `overrides` | explicit `{modId, pawnDescription}` replacements: your entry **replaces** that pack's entry on that pawn. Declared = consensual; without it, the clash is a conflict and the first-loaded entry wins. |

### Write only the keys you set — and don't copy ENC's pack as a template

A model entry needs **`resourceName` and `pawnDescription`**; everything else is optional. A key you omit falls back
to that field's default in `HafModelSchema` — the one authoritative default for both halves — which is why
[`haf-pack.example.json`](haf-pack.example.json) is a working retexture pack in **five keys**. Write what you set,
nothing more.

That matters because **ENC's own shipped `pack.json` is a bad thing to learn the format from.** It carries roughly
200 lines per entry, and about **55 of those keys per entry are bake-time-only** — `size`, `rotation`,
`normalsMode`, `stripParts`, `convertGrid`, `targetTris`, the `deploy*` tuning, the `animClip*` names — read by the
Model Factory when it bakes and **never read by the runtime at all**. They are in that file because it doubles as
the editor's authoring database, and Unity's `JsonUtility` always writes every field of the class it serializes.
They are not part of the pack contract, and setting them in a hand-written pack does nothing.

The split is structural, so you can always check it rather than guess:

| In a pack file | What it is |
|---|---|
| Every field on **`Haf.Schema.HafModelSchema`** | **the contract** — the behaviour/sound/prop/tint config both halves read |
| The **`int[]` GUID arrays** (`skel`, `atlas`, `clip*`) | **the contract** — asset handles the Factory fills in; omit them entirely for a runtime-only entry |
| Everything else `ModelDef` declares (non-`int[]`) | **bake-time only** — ignored by the runtime |

`Tools/check_schema_parity.sh` prints the current bake-time-only list under its `INFO` line, so the set is derived
from the code rather than from this page. See [Shared-Schema.md](Shared-Schema.md) for the field taxonomy.

**Backward compatible:** a legacy bare `{ "models": [...] }` with no wrapper still loads — it just gets default metadata
(`modId` = the filename, `schemaVersion` = 0). A legacy `haf_models.json` base file, if one exists, still loads too;
ENC itself now ships as a normal pack at `haf_packs/ENCReload/pack.json` (`modId` `enc`, module `ENCReload`).

### The schema contract

The registry schema **evolves additively**: new keys get added, existing keys keep their name, type and meaning. That
is what makes the version safe in both directions — a key your pack never wrote falls through to its default, and a key
this HAF doesn't know is stripped before it can touch anything.

So `schemaVersion` is an **advisory, never a gate**. Nothing HAF does with it can cost your pack its place:

| Your `schemaVersion` | What happens |
|---|---|
| absent (`0`) | Loads normally. A line in `RESOLUTION` notes it's unversioned — no warning; legacy packs predate the field. |
| `1` … the version HAF implements | Loads normally, silently. This is the ordinary case. |
| **newer than HAF implements** | **Loads, with a warning.** Every key this build knows still works, but keys added after its version are stripped and ignored — so dials you set may silently do nothing. The remedy is to update HAF. |
| below the oldest readable version | Loads, with a warning to re-bake. Reserved for the day a field's *meaning* changes rather than a field being added — the one break the additive contract doesn't cover. |

The version HAF implements is printed in the `haf_load_report.txt` header (`schema implemented=1 (reads 1+)`), directly
above each pack's own `schemaVersion=` line, so the comparison is one glance. It is defined once, in
`Haf.Schema.HafSchema.Version`, and the push gate fails if this page or the example pack quotes a different number.

**If you author a pack:** set `schemaVersion` to the version you built against and leave it alone. Bumping it doesn't
unlock anything — it only tells an older HAF that it's older, which is exactly the signal a user needs when your unit
loads but your new dial appears to do nothing.

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
packs=2  models=14  conflicts=0  overrides applied=0  schema implemented=1 (reads 1+)

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
