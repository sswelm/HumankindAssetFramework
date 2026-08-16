# Shared schema library — migration plan

**Status: designed + POC-proven, not yet built.** A plan to collapse the duplicated model schema into one shared
definition. This addresses the **duplication** half of the `ModelEntry`/`ModelDef` god-object (finding A3, the 4-place
schema drift) — *not* the size half (that's the separately-declined POCO decomposition).

## The problem
The model schema is written in **three** hand-synced places that drift silently:
- `ModelDef` (editor, `ENCReload/Assets/Scripts/Editor/ModelRegistry.cs`) — **128 fields**, serialized by Unity `JsonUtility`.
- `ModelEntry` (plugin, `Patches/UniversalInjectPatch.cs`) — **148 fields** = the ~79 it reads + ~20 runtime-state fields.
- The plugin's **two** parse paths (`ParseModels` Newtonsoft + the regex fallback) — each hand-lists the read keys.

Today the drift is caught by `Tools/check_schema_parity.sh` (a *test*). Standing up the pre-push gate found three
real drifts this class had already produced. The goal is to make drift **impossible by construction** instead.

## The design (POC-proven — see `tools/schema-poc/`)
One shared **netstandard2.0** `[Serializable]` class = the **editor↔plugin contract** (the fields the plugin reads):

```
Haf.Schema (netstandard2.0)
  [Serializable] class HafModelSchema   // the ~79 contract fields, defined ONCE

ENCReload (editor):   class ModelDef : HafModelSchema { …+49 bake-time-only fields… }   // JsonUtility serializes all
ENCAccessProof (plugin): class ModelEntry : HafModelSchema { …+~20 runtime-state fields… }  // Newtonsoft fills inherited
```

Because both consumers **inherit** the shared class, the hundreds of `e.<field>` / `def.<field>` call sites **don't
change** — the churn that made the POCO split not worth it is avoided. The POC verified every risky assumption:
- netstandard2.0 is consumable by the net471 plugin (it already consumes netstandard2.0 via Newtonsoft) and by Unity.
- Newtonsoft round-trips the shared type and fills **inherited** fields (plugin side, headless).
- Unity `JsonUtility` serializes the shared type **and its inherited fields** in the same JSON shape (editor side, in-Unity).

## Decisions to lock at build time
- **Split line:** `HafModelSchema` = exactly the plugin's read set (the parity script already enumerates the ~79 read
  keys and the 128 written fields → shared = the 79; editor-only bake fields = the other 49; runtime-state = the ~20
  `ModelEntry` fields not populated from JSON — `texOwned`, `coreDesc`, resolved handles, `*AnimId`, the poll dicts, …).
- **`Vector3` fields** (`rotation`/`position`): **reference `UnityEngine`** in the shared lib (both consumers have it;
  preserves the exact `{"x":…,"y":…,"z":…}` JSON shape for free). The alternative — plain floats — decouples the lib
  but changes the on-disk format, so it's rejected.
- **GUID fields** (`skel`/`atlas`/`clip…`): stay `int[]` in the schema (the on-disk shape); the plugin's *resolved*
  handles are runtime-state on `ModelEntry`, not schema.

## Steps (ordered)
1. **Create `Haf.Schema`** (netstandard2.0, references `UnityEngine.CoreModule` via a Managed-folder HintPath like the
   plugin does). Move the ~79 contract fields into `HafModelSchema` verbatim (names/types/defaults unchanged, so the
   JSON is byte-identical).
2. **Editor:** `ModelDef : HafModelSchema`, keeping only the 49 bake-time-only fields on `ModelDef`. `RegistryFile.models`
   stays `List<ModelDef>`.
3. **Plugin:** `ModelEntry : HafModelSchema`, keeping only the ~20 runtime-state fields. `ParseModels` Newtonsoft path
   deserializes into `ModelEntry` (inherited fields fill automatically).
4. **Regex fallback:** either keep it (still works) or — the root-cause finish — **reflection-generate** it over
   `HafModelSchema`'s fields, deleting the last hand-list.
5. **Build/deploy wiring:**
   - Plugin `.csproj` references `Haf.Schema`; its build output ships `Haf.Schema.dll` to `BepInEx/plugins/` (BepInEx
     loads every DLL there). Update `Tools/haf-deploy.bat` + the headless-CLI `build-mod` to copy it.
   - Editor: `Haf.Schema.dll` in `ENCReload/Assets/Plugins/`; add it to `Tools/editor_compile_check.rsp` so the gate
     compiles the editor scripts against it.
6. **Verification (all must pass before merge):** `dotnet build` + `dotnet test` (the 59 tests) + a new shared-schema
   parse test; `editor_compile_check`; a **bake + in-game launch** proving pack.json round-trips identically and units
   still inject; `bindcheck` unaffected (it's the reflection catalog, not the schema).
7. **Retire `check_schema_parity.sh`:** once `ModelDef` and `ModelEntry` share `HafModelSchema`, parity is
   compiler-guaranteed — remove the guard from `Tools/check.sh` (or leave it as a trivially-green belt-and-braces).

## Risks & mitigations
- **JsonUtility inherited-field serialization** — the one Unity unknown — **proven** by the POC.
- **Field-split reconciliation** (schema vs runtime-state; the `int[]` GUID vs resolved handle) — the main mechanical
  work; do it field-by-field with the parity script as the oracle *during* migration.
- **Extra deployed DLL** — a new moving part; the deploy scripts + CLI must copy it, and a missing `Haf.Schema.dll` in
  `plugins/` would fail plugin load loudly (BepInEx names the missing dependency).
- **Rollback** — do it on a branch; the in-game bake+launch gate is the merge bar (main/master only takes verified work).

## What this does NOT do
It removes **duplication**, not **size** — one shared 128-ish-field class is still a god-object, just defined once.
Reducing size means grouping the schema into nested POCOs by concern (geometry / animation / deploy / sound / …) inside
`Haf.Schema` — optional, separable, and closer to the declined split, so out of scope here.

## Sequencing & payoff
Pairs naturally with the **2.0 packaging / editor-ENC decoupling** (#5) — both touch project structure and build/deploy,
and a neutral shared schema is a step toward the distributable framework. Payoff: **one** place to add a field, the
schema-parity guard *deleted* rather than maintained, and Architecture **7.5 → ~8.5** — without the hot-path churn.
