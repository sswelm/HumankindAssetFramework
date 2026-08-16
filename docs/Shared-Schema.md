# Shared schema library (`Haf.Schema`)

The model schema's shared fields are defined **once**, in a netstandard2.0 library both halves inherit — so the editor
and the plugin can't drift on them.

## Shape
- **`Haf.Schema.HafModelSchema`** (`Haf.Schema/HafModelSchema.cs`, netstandard2.0, no dependencies) holds the **64 fields
  stored identically** by the editor and the plugin — the behavioral / sound / prop / tint config.
- **`ModelDef`** (editor, `ENCReload/Assets/Scripts/Editor/ModelRegistry.cs`) `: HafModelSchema` — adds its bake-time,
  GUID (`int[]`), and `Vector3` fields.
- **`ModelEntry`** (plugin, `Patches/UniversalInjectPatch.cs`) `: HafModelSchema` — adds its runtime-state and GUID
  (`sa/sb/..`) fields.

Both classes inherit, so the shared fields are still used by name (`cur.<field>`, `e.<field>`) exactly as before — no
call-site changes.

## What's shared, what isn't
| Fields | Where |
|---|---|
| ~64 identical fields (string/bool/float/int) | **`HafModelSchema`** — one definition, compiler-enforced |
| GUIDs | **not shared** — `int[] skel/atlas/clip` (editor) vs `sa,sb,sc,sd` (plugin): different runtime shapes |
| Bake-time-only (`size`, `convertGrid`, `stripParts`, …) | `ModelDef` only |
| Runtime state (resolved handles, `*AnimId`, session flags, poll dicts) | `ModelEntry` only |

The **divergent** fields (GUIDs + the two class-specific sets) are still hand-synced across the plugin's two parse paths
(Newtonsoft object parse + regex fallback in `ParseModels`) and checked by `check_schema_parity.sh`, which unions
`HafModelSchema`'s fields into the write set before asserting *plugin-reads ⊆ writes* and *cast-types agree*.

## Adding a field
- **Shared** (both read it, same type) — add it to `HafModelSchema` once. Both inherit it; nothing else to touch.
- **Editor-only** (bake-time) — add to `ModelDef`.
- **Plugin-only** (runtime) — add to `ModelEntry` + both parse paths, then run `check_schema_parity.sh`.

## Serialization
- **Editor** writes `pack.json` via Unity `JsonUtility`, which serializes inherited public fields.
- **Plugin** reads via Newtonsoft (with a regex fallback), deserializing into `ModelEntry` — inherited fields fill by name.

## Build & deploy
`HafModelSchema` builds to **`Haf.Schema.dll`** (netstandard2.0), referenced by the plugin via a `ProjectReference`
(`Private=true`), so `dotnet build` emits it into `bin/Release` alongside the plugin. **The plugin depends on it** —
BepInEx won't load the plugin without `Haf.Schema.dll` in `BepInEx/plugins/`, so deploy both with
`tools/deploy-plugin.sh`. The editor consumes the same DLL from `ENCReload/Assets/Plugins/HafSchema/Haf.Schema.dll`.
