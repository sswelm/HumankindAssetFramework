# Design note — pack pre-flight validator (third-party author DX)

**Status: BUILT 2026-08-18 (Phases 1 + 2), per this design.** The pure rule core lives in the shared schema DLL
(`Haf.Schema.PackValidator` — one rule set, ~30 rules over files/bones/pawn/formats/ranges/exclusions, 19 unit
tests) with the tri-state `IValidationContext` exactly as specified (null = "this host can't check" → skipped,
never guessed). The two thin hosts: the **Model Factory's "Validate pack" button** (editor context: the Pick
list's pawn names, deployed-pack + legacy file dirs, bones from each entry's baked skeleton asset) and the
**plugin's boot-time pass** (`UniversalInject.RunPreflight`, once per process after registration — bones against
the LOADED skeleton, files on the player's disk, plus the plugin-only authored-GUID-didn't-resolve checks —
appending `## Pre-flight` to `haf_load_report.txt`). Severity semantics as designed: warnings explain, nothing is
blocked. Phase 3 (vertex-budget hints, JSON-schema file) remains open. Original design below.

## The gap this closes

Today a HAF pack fails in two very different ways depending on the mistake:

- **Structure mistakes are already loud and human-readable.** Malformed pack JSON, duplicate `modId`, missing
  `dependsOn`, ordering cycles, and same-pawn conflicts all produce plain-language warnings in the BepInEx log and the
  `haf_load_report.txt` (implemented in the 07-14 / 07-19 multi-mod work). Bad input **fails soft** — never a crash.
- **Content mistakes fail *silently*.** A wrong bone name (`muzzleBone: "Turrret"`), an unresolvable asset GUID, or a
  missing texture/WAV path doesn't warn — the feature just doesn't happen. For someone authoring a pack on their own
  machine, "the flash is in the wrong place and I don't know why" is exactly the friction that stops third-party
  adoption.

The validator turns silent content failures into named, actionable messages **before** (or at) load.

## What to validate (per registry entry)

Grounded in the fields a `ModelEntry` / district / prop / projectile entry actually references:

| Class | Fields | Check | Where it's checkable |
|---|---|---|---|
| **Asset GUIDs** | `skeleton`, `atlas`, `clip`, `moveClip`/`idleClip`/… , district FxMesh, prop MeshCollection | the GUID resolves to a loaded asset of the right kind | **boot-time** (needs the game's asset DB) |
| **Bone names** | `muzzleBone`, `turretBone`, `animateBones`, hand-prop bone | the named bone exists in the entry's skeleton | **editor** (baker knows the model's bones) **and boot-time** (against the loaded skeleton) |
| **File paths** | `soundFile`, `soundStart/Stop/Idle/Attack/Death/Battle`, PNG skins | the file exists in the pack folder | **editor and boot-time** (plain file existence) |
| **Target unit** | `pawnDescription` | matches a real game unit descriptor | **editor** (pawn dropdown) **and boot-time** (unit catalog) |
| **Schema** | field names, types, enums (`materialMode`), numeric ranges | keys are known, types/enums valid, ranges sane | **editor** (pre-ship) |

Note the split: **file/bone/pawn/schema** checks are doable in the **editor** (before the pack ever ships — the best
DX), while **GUID resolution** and **bone-against-loaded-skeleton** need the running game, so they belong to a
**boot-time** pass on the end user's machine.

## Where the checks live (two surfaces, one core)

Factor the rules into one pure `ValidateEntry(entry, context)` returning a list of `(severity, message)` — mirrors the
existing `FormationOverridePatch.Validate(Entry)` pattern and stays unit-testable (it's pure logic over data + a small
lookup interface). Two thin callers:

1. **Editor button — "Validate pack"** in the pack/Factory window. Runs the checks it *can* without the game
   (files exist, bones exist in the inspected model, pawn is real, schema/enums/ranges). This is the pre-ship gate — an
   author sees problems before distributing.
2. **Boot-time pre-flight pass** in the plugin, right after pack resolution and asset registration. Runs the checks that
   need the loaded game (GUID resolution, bone-against-actual-skeleton, file existence on the *user's* disk) and appends
   a **`## Pre-flight`** section to `haf_load_report.txt` plus a summary log line.

## Message format

One line per problem, always naming pack + entry + the specific fault + (where cheap) the valid options:

```
[Preflight] pack 'coolmod' entry 'PanzerIV' (pawn 'AttackHelicopter'):
    bone 'Turrret' not found in skeleton — available: Root, Body, Turret, Barrel, Muzzle
    texture 'skins/panzer.png' not found in pack folder
    clip GUID 3f2a… did not resolve to a ClipCollection (was it baked?)
```

- **Severity:** `warning` = the feature degrades but the pack still loads (today's fail-soft behaviour, now *explained*);
  `error` = the entry is unusable and skipped. Default to `warning` — never regress the "bad input never crashes" rule.
- Summary line: `[Preflight] pack 'coolmod': 2 warning(s), 0 error(s) — see haf_load_report.txt`.

## Phasing

- **Phase 1 (cheap, most of the value):** the editor "Validate pack" button — file existence, bone-in-inspected-model,
  pawn-exists, schema/enum/range. Catches the common typos before a pack ever ships, with zero runtime risk.
- **Phase 2:** the boot-time pass — GUID resolution + bone-against-loaded-skeleton + on-disk file existence → load
  report. Catches what only the end user's machine can (a bad shipped GUID, a case-wrong path).
- **Phase 3 (optional):** vertex-budget hints (warn when a pack's model types approach the mesh-buffer ceiling — see
  [Vertex-Budget](Vertex-Budget.md)) and a JSON-schema file authors can validate against in their own editor.

## Non-goals / relationship to existing tools

- **Not** `check_schema_parity.sh` — that's a *dev-side* guard against `ModelDef`↔`ModelEntry` source drift across the
  two repos. This validator is *author-side*, against a concrete pack's data.
- **Not** a replacement for fail-soft — it *explains* failures; the runtime still degrades gracefully on anything it
  misses.
- Fits the "guided, not guessy" design goal and the [Multi-Mod](Multi-Mod.md) pack contract; it's the author-facing
  bookend to the load report.
