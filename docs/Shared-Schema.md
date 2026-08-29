# Shared schema library (`Haf.Schema`)

The model schema's shared fields are defined **once**, in a netstandard2.0 library both halves inherit — so the editor
and the plugin can't drift on them.

> **This page owns the field count.** It is the only doc that states a number — the count drifted to three different
> values across four docs before 2026-08-20; everywhere else now says "the shared fields" and links here. Re-check it
> with `grep -cE '^\s+public ' Haf.Schema/HafModelSchema.cs` (minus the class declaration line). The *real* guard is
> `check_schema_parity.sh`, not the number written here.

## Shape
- **`Haf.Schema.HafModelSchema`** (`Haf.Schema/HafModelSchema.cs`, netstandard2.0; references `UnityEngine.CoreModule`
  for the shared `Vector3`) holds the **70 fields stored identically** by the editor and the plugin — the behavioral /
  sound / prop / tint config, plus `resourceName`, `pawnDescription`, and the `position` offset (`Vector3`).
- **`ModelDef`** (editor, `editor/ModelRegistry.cs`) `: HafModelSchema` — adds its bake-time,
  GUID (`int[]`), and editor-only `Vector3` (`rotation`) fields.
- **`ModelEntry`** (plugin, `Patches/UniversalInjectPatch.cs`) `: HafModelSchema` — adds its runtime-state and GUID
  (`sa/sb/..`) fields.

Both classes inherit, so the shared fields are still used by name (`cur.<field>`, `e.<field>`) exactly as before — no
call-site changes.

## What's shared, what isn't
| Fields | Where |
|---|---|
| 70 identical fields (string/bool/float/int + the `position` `Vector3`) | **`HafModelSchema`** — one definition, compiler-enforced |
| GUIDs | **not shared** — `int[] skel/atlas/clip` (editor) vs `sa,sb,sc,sd` (plugin): different runtime shapes |
| Bake-time-only (`size`, `convertGrid`, `stripParts`, …) | `ModelDef` only |
| Runtime state (resolved handles, `*AnimId`, session flags, poll dicts) | `ModelEntry` only |

The plugin's **primary parse is generic**: `ParseModels` deserializes each model with `m.ToObject<ModelEntry>()`, so every
name-matching field (all 70 shared + any plugin-own config) maps automatically — no hand-list. Only two shapes stay
explicit: the **GUID arrays** (one JSON array `skel[]` → four ints `sa/sb/sc/sd`, etc.) and **`position`** (a
`UnityEngine.Vector3`, which Newtonsoft can't deserialize — its `normalized` property self-references — so the key is
stripped from the object before `ToObject` and re-pinned by hand). The per-entry **regex fallback** (for malformed
JSON) still hand-lists every field; `check_schema_parity.sh` asserts it covers the GUID hand-list + every shared field,
and that everything it reads is a field the baker writes. A **missing key** falls to the field's initializer in
`HafModelSchema` — the one authoritative default for both halves.

## Adding a field
- **Shared** (both read it, same type) — add it to `HafModelSchema` once (with its default as the initializer). Both
  inherit it and the plugin's primary parse maps it automatically; add the matching `Regex.Matches` line to the fallback,
  then run `check_schema_parity.sh` (it fails loudly if the fallback lags).
- **Editor-only** (bake-time) — add to `ModelDef`.
- **Plugin-only CONFIG** (pack-authored, like `rotorSpinBones`) — add to `ModelEntry` **and to the
  `registryConfigKeys` whitelist** in `ParseModels`: since 2026-08-17 the primary parse strips every
  non-whitelisted key before the generic map (so pack JSON can never bind runtime-STATE fields like `repointed` —
  test-pinned). A forgotten whitelist entry fails loud (the key is stripped and Diag names it). If the field must
  survive malformed JSON too, add it to the regex fallback + the parity allowlist.
- **Plugin-only runtime STATE** — just add the field to `ModelEntry`; it is protected from JSON automatically
  (not whitelisted = stripped). No attribute, no list to remember.

## Serialization
- **Editor** writes `pack.json` via Unity `JsonUtility`, which serializes inherited public fields (and always writes
  every field — no omitted keys).
- **Plugin** reads via Newtonsoft's generic `ToObject<ModelEntry>()` (inherited fields fill by name; GUID arrays and
  `position` extracted by hand), with the index-aligned regex fallback for malformed JSON.

## Build & deploy
`HafModelSchema` builds to **`Haf.Schema.dll`** (netstandard2.0), referenced by the plugin via a `ProjectReference`
(`Private=true`), so `dotnet build` emits it into `bin/Release` alongside the plugin. **The plugin depends on it** —
BepInEx won't load the plugin without `Haf.Schema.dll` in `BepInEx/plugins/`, so deploy both with
`tools/deploy-plugin.sh`. The editor consumes the same DLL from `editor/Plugins/Haf.Schema.dll`.
