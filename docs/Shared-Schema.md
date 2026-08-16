# Shared schema library (`Haf.Schema`)

The model schema's shared fields are defined **once**, in a netstandard2.0 library both halves inherit — so the editor
and the plugin can't drift on them.

## Shape
- **`Haf.Schema.HafModelSchema`** (`Haf.Schema/HafModelSchema.cs`, netstandard2.0; references `UnityEngine.CoreModule`
  for the shared `Vector3`) holds the **66 fields stored identically** by the editor and the plugin — the behavioral /
  sound / prop / tint config, plus `resourceName`, `pawnDescription`, and the `position` offset (`Vector3`).
- **`ModelDef`** (editor, `ENCReload/Assets/Scripts/Editor/ModelRegistry.cs`) `: HafModelSchema` — adds its bake-time,
  GUID (`int[]`), and editor-only `Vector3` (`rotation`) fields.
- **`ModelEntry`** (plugin, `Patches/UniversalInjectPatch.cs`) `: HafModelSchema` — adds its runtime-state and GUID
  (`sa/sb/..`) fields.

Both classes inherit, so the shared fields are still used by name (`cur.<field>`, `e.<field>`) exactly as before — no
call-site changes.

## What's shared, what isn't
| Fields | Where |
|---|---|
| 66 identical fields (string/bool/float/int + the `position` `Vector3`) | **`HafModelSchema`** — one definition, compiler-enforced |
| GUIDs | **not shared** — `int[] skel/atlas/clip` (editor) vs `sa,sb,sc,sd` (plugin): different runtime shapes |
| Bake-time-only (`size`, `convertGrid`, `stripParts`, …) | `ModelDef` only |
| Runtime state (resolved handles, `*AnimId`, session flags, poll dicts) | `ModelEntry` only |

The plugin's **primary parse is generic**: `ParseModels` deserializes each model with `m.ToObject<ModelEntry>()`, so every
name-matching field (all 66 shared + any plugin-own config) maps automatically — no hand-list. Only two shapes stay
explicit: the **GUID arrays** (one JSON array `skel[]` → four ints `sa/sb/sc/sd`, etc.) and **`position`** (a
`UnityEngine.Vector3`, which Newtonsoft can't deserialize — its `normalized` property self-references — so the key is
stripped from the object before `ToObject` and re-pinned by hand). The index-aligned **regex fallback** (for malformed
JSON) still hand-lists every field; `check_schema_parity.sh` asserts it covers the GUID hand-list + every shared field,
and that everything it reads is a field the baker writes. A **missing key** falls to the field's initializer in
`HafModelSchema` — the one authoritative default for both halves.

## Adding a field
- **Shared** (both read it, same type) — add it to `HafModelSchema` once (with its default as the initializer). Both
  inherit it and the plugin's primary parse maps it automatically; add the matching `Regex.Matches` line to the fallback,
  then run `check_schema_parity.sh` (it fails loudly if the fallback lags).
- **Editor-only** (bake-time) — add to `ModelDef`.
- **Plugin-only** (runtime) — add to `ModelEntry`; the primary parse maps it by name automatically. If it must survive
  malformed JSON too, add it to the regex fallback + the parity allowlist.

## Serialization
- **Editor** writes `pack.json` via Unity `JsonUtility`, which serializes inherited public fields (and always writes
  every field — no omitted keys).
- **Plugin** reads via Newtonsoft's generic `ToObject<ModelEntry>()` (inherited fields fill by name; GUID arrays and
  `position` extracted by hand), with the index-aligned regex fallback for malformed JSON.

## Build & deploy
`HafModelSchema` builds to **`Haf.Schema.dll`** (netstandard2.0), referenced by the plugin via a `ProjectReference`
(`Private=true`), so `dotnet build` emits it into `bin/Release` alongside the plugin. **The plugin depends on it** —
BepInEx won't load the plugin without `Haf.Schema.dll` in `BepInEx/plugins/`, so deploy both with
`tools/deploy-plugin.sh`. The editor consumes the same DLL from `ENCReload/Assets/Plugins/HafSchema/Haf.Schema.dll`.
